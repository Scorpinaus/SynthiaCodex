using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class PermissionGrantOptionViewModel(string name) : ObservableObject
{
    private bool isGranted = true;

    public string Name { get; } = name;

    public bool IsGranted
    {
        get => isGranted;
        set => SetProperty(ref isGranted, value);
    }
}

public sealed class ApprovalPromptViewModel
{
    public ApprovalPromptViewModel(CodexServerRequest request)
    {
        Request = request;
        PermissionOptions = request.Payload is CodexPermissionApprovalRequest permissions
            ? [.. permissions.RequestedPermissions.Select(property => new PermissionGrantOptionViewModel(property.Key))]
            : [];
    }

    public CodexServerRequest Request { get; }

    public IReadOnlyList<PermissionGrantOptionViewModel> PermissionOptions { get; }

    public string Kind => Request.Payload switch
    {
        CodexCommandApprovalRequest => "Command execution",
        CodexFileChangeApprovalRequest => "File changes",
        CodexPermissionApprovalRequest => "Additional permissions",
        _ => "Server request"
    };

    public string Reason => Request.Payload switch
    {
        CodexCommandApprovalRequest command => command.Reason ?? "Codex requested permission to run a command.",
        CodexFileChangeApprovalRequest file => file.Reason ?? "Codex requested permission to change files.",
        CodexPermissionApprovalRequest permissions => permissions.Reason ?? "Codex requested additional permissions.",
        _ => "Codex requested a decision."
    };

    public string Detail => Request.Payload switch
    {
        CodexCommandApprovalRequest command => command.Command ?? "Command details were not provided.",
        CodexFileChangeApprovalRequest file => file.GrantRoot ?? "File scope was not provided.",
        CodexPermissionApprovalRequest permissions => permissions.RequestedPermissions.ToJsonString(),
        _ => Request.Method
    };

    public string Context => Request.Payload switch
    {
        CodexCommandApprovalRequest command when command.NetworkContext is not null =>
            $"{command.Cwd ?? "Unknown working directory"} · Network: {command.NetworkContext.Protocol}://{command.NetworkContext.Host}",
        CodexCommandApprovalRequest command => command.Cwd ?? "Unknown working directory",
        CodexFileChangeApprovalRequest file => file.GrantRoot ?? "Unknown file scope",
        CodexPermissionApprovalRequest permissions => permissions.Cwd,
        _ => string.Empty
    };

    public bool IsPermissionRequest => Request.Payload is CodexPermissionApprovalRequest;

    public JsonObject BuildGrantedPermissions()
    {
        var result = new JsonObject();
        if (Request.Payload is not CodexPermissionApprovalRequest permissions)
        {
            return result;
        }

        foreach (var option in PermissionOptions.Where(option => option.IsGranted))
        {
            if (permissions.RequestedPermissions[option.Name] is { } requestedValue)
            {
                result[option.Name] = requestedValue.DeepClone();
            }
        }

        return result;
    }
}

public sealed class ApprovalQueueViewModel : ObservableObject
{
    private readonly Func<CodexServerRequest, CodexServerRequestResponse, CancellationToken, Task> responder;
    private readonly ObservableCollection<ApprovalPromptViewModel> pending = [];
    private readonly AsyncRelayCommand allowOnceCommand;
    private readonly AsyncRelayCommand allowSessionCommand;
    private readonly AsyncRelayCommand declineCommand;
    private readonly AsyncRelayCommand cancelCommand;
    private ApprovalPromptViewModel? activePrompt;
    private bool isResponding;
    private string? errorMessage;

    public ApprovalQueueViewModel(
        Func<CodexServerRequest, CodexServerRequestResponse, CancellationToken, Task> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        this.responder = responder;
        allowOnceCommand = new AsyncRelayCommand(() => RespondAsync(CodexApprovalDecision.Accept), CanRespond);
        allowSessionCommand = new AsyncRelayCommand(() => RespondAsync(CodexApprovalDecision.AcceptForSession), CanRespond);
        declineCommand = new AsyncRelayCommand(() => RespondAsync(CodexApprovalDecision.Decline), CanRespond);
        cancelCommand = new AsyncRelayCommand(() => RespondAsync(CodexApprovalDecision.Cancel), CanRespond);
    }

    public ReadOnlyObservableCollection<ApprovalPromptViewModel> Pending => new(pending);

    public ApprovalPromptViewModel? ActivePrompt
    {
        get => activePrompt;
        private set
        {
            if (SetProperty(ref activePrompt, value))
            {
                OnPropertyChanged(nameof(HasPendingApproval));
                RaiseCommandStates();
            }
        }
    }

    public int PendingCount => pending.Count;

    public bool HasPendingApproval => ActivePrompt is not null;

    public bool IsResponding
    {
        get => isResponding;
        private set
        {
            if (SetProperty(ref isResponding, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public ICommand AllowOnceCommand => allowOnceCommand;

    public ICommand AllowSessionCommand => allowSessionCommand;

    public ICommand DeclineCommand => declineCommand;

    public ICommand CancelCommand => cancelCommand;

    public void Enqueue(CodexServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (pending.Any(prompt => prompt.Request.RequestId == request.RequestId))
        {
            return;
        }

        pending.Add(new ApprovalPromptViewModel(request));
        OnPropertyChanged(nameof(PendingCount));
        ActivePrompt ??= pending[0];
    }

    public bool Resolve(CodexRequestId requestId)
    {
        var prompt = pending.FirstOrDefault(candidate => candidate.Request.RequestId == requestId);
        if (prompt is null)
        {
            return false;
        }

        pending.Remove(prompt);
        OnPropertyChanged(nameof(PendingCount));
        ActivePrompt = pending.FirstOrDefault();
        ErrorMessage = null;
        return true;
    }

    public async Task RespondAsync(
        CodexApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        var prompt = ActivePrompt;
        if (prompt is null || IsResponding)
        {
            return;
        }

        var response = CreateResponse(prompt, decision);
        await SendResponseAsync(prompt, response, cancellationToken).ConfigureAwait(true);
    }

    public async Task ApprovePermissionsAsync(
        CodexPermissionGrantScope scope,
        CancellationToken cancellationToken = default)
    {
        var prompt = ActivePrompt;
        if (prompt?.Request.Payload is not CodexPermissionApprovalRequest permissions || IsResponding)
        {
            return;
        }

        await SendResponseAsync(
            prompt,
            CodexServerRequestResponse.Permissions(prompt.BuildGrantedPermissions(), scope),
            cancellationToken).ConfigureAwait(true);
    }

    public void Clear()
    {
        pending.Clear();
        OnPropertyChanged(nameof(PendingCount));
        ActivePrompt = null;
        ErrorMessage = null;
    }

    private bool CanRespond() => ActivePrompt is not null && !IsResponding;

    private async Task SendResponseAsync(
        ApprovalPromptViewModel prompt,
        CodexServerRequestResponse response,
        CancellationToken cancellationToken)
    {
        IsResponding = true;
        ErrorMessage = null;
        try
        {
            await responder(prompt.Request, response, cancellationToken).ConfigureAwait(true);
            Resolve(prompt.Request.RequestId);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsResponding = false;
        }
    }

    private static CodexServerRequestResponse CreateResponse(
        ApprovalPromptViewModel prompt,
        CodexApprovalDecision decision) => prompt.Request.Payload switch
    {
        CodexCommandApprovalRequest => CodexServerRequestResponse.Command(decision),
        CodexFileChangeApprovalRequest => CodexServerRequestResponse.FileChange(decision),
        CodexPermissionApprovalRequest => CodexServerRequestResponse.Permissions(
            decision is CodexApprovalDecision.Accept or CodexApprovalDecision.AcceptForSession
                ? prompt.BuildGrantedPermissions()
                : new JsonObject(),
            decision == CodexApprovalDecision.AcceptForSession
                ? CodexPermissionGrantScope.Session
                : CodexPermissionGrantScope.Turn),
        _ => throw new InvalidOperationException($"Unsupported approval request: {prompt.Request.Method}")
    };

    private void RaiseCommandStates()
    {
        allowOnceCommand.RaiseCanExecuteChanged();
        allowSessionCommand.RaiseCanExecuteChanged();
        declineCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
    }
}
