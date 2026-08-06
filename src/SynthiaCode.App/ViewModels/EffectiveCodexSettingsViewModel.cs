using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Logging;
using SynthiaCode.Harnesses.Codex;

namespace SynthiaCode.App.ViewModels;

public sealed record EffectiveCodexSettingItem(string Label, string Value, string Origin);

public sealed class EffectiveCodexSettingsViewModel : ObservableObject
{
    private readonly ICodexConfigurationFeature coordinator;
    private readonly Func<string?> activeWorkspacePath;
    private readonly Func<bool> isShuttingDown;
    private readonly IAppLogger logger;
    private readonly AsyncRelayCommand refreshCommand;
    private CancellationTokenSource? refreshCancellation;
    private string contextPath = string.Empty;
    private string message = "Open Settings to inspect the effective Codex configuration.";
    private bool isBusy;
    private bool isStale = true;
    private bool isSupported = true;
    private long refreshGeneration;

    public EffectiveCodexSettingsViewModel(
        ICodexConfigurationFeature coordinator,
        Func<string?> activeWorkspacePath,
        Func<bool> isShuttingDown,
        IAppLogger logger)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.activeWorkspacePath = activeWorkspacePath ?? throw new ArgumentNullException(nameof(activeWorkspacePath));
        this.isShuttingDown = isShuttingDown ?? throw new ArgumentNullException(nameof(isShuttingDown));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RefreshCommand = refreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(),
            () => !isShuttingDown() && !IsBusy);
    }

    public ObservableCollection<EffectiveCodexSettingItem> Items { get; } = [];

    public ICommand RefreshCommand { get; }

    public string ContextPath
    {
        get => contextPath;
        private set => SetProperty(ref contextPath, value);
    }

    public string Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsStale
    {
        get => isStale;
        private set => SetProperty(ref isStale, value);
    }

    public bool IsSupported
    {
        get => isSupported;
        private set => SetProperty(ref isSupported, value);
    }

    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        UpdateContext();
        if (IsStale)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (isShuttingDown())
        {
            return;
        }

        UpdateContext();
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = refreshCancellation.Token;
        var generation = Interlocked.Increment(ref refreshGeneration);
        var requestedPath = ContextPath;

        IsBusy = true;
        Message = "Loading effective Codex settings...";
        try
        {
            var configuration = await coordinator
                .ReadEffectiveConfigurationAsync(
                    string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath,
                    token)
                .ConfigureAwait(true);
            if (generation != Volatile.Read(ref refreshGeneration) ||
                !string.Equals(
                    requestedPath,
                    NormalizePath(activeWorkspacePath()),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IsSupported = configuration.IsSupported;
            Items.Clear();
            if (!configuration.IsSupported)
            {
                IsStale = false;
                Message = "This Codex app-server version does not expose effective settings.";
                return;
            }

            Add("Model", configuration.Model, "model", configuration);
            Add("Model provider", configuration.ModelProvider, "model_provider", configuration);
            Add("Reasoning effort", configuration.ReasoningEffort, "model_reasoning_effort", configuration);
            Add("Service tier", configuration.ServiceTier, "service_tier", configuration);
            Add("Profile", configuration.Profile, "profile", configuration);
            Add("Sandbox mode", configuration.SandboxMode, "sandbox_mode", configuration);
            Add("Approval policy", configuration.ApprovalPolicy, "approval_policy", configuration);
            Add("Approvals reviewer", configuration.ApprovalsReviewer, "approvals_reviewer", configuration);
            Add("Web search", configuration.WebSearchMode, "web_search", configuration);
            Add(
                "Workspace network",
                configuration.SandboxNetworkAccess switch
                {
                    true => "Allowed",
                    false => "Blocked",
                    null => null
                },
                "sandbox_workspace_write.network_access",
                configuration);

            IsStale = false;
            Message = "Effective settings are read-only and redacted to a safe allowlist.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            IsStale = true;
            Message = $"Effective Codex settings could not be loaded: {ex.Message}";
            logger.Log(
                AppLogLevel.Warning,
                "effective_codex_settings_refresh_failed",
                "Effective Codex settings could not be refreshed.",
                exception: ex);
        }
        finally
        {
            if (generation == Volatile.Read(ref refreshGeneration))
            {
                IsBusy = false;
            }
        }
    }

    public void NotifyContextChanged()
    {
        UpdateContext();
        IsStale = true;
    }

    public void RaiseCommandStates() => refreshCommand.RaiseCanExecuteChanged();

    private void Add(
        string label,
        string? value,
        string key,
        CodexEffectiveConfiguration configuration)
    {
        configuration.Origins.TryGetValue(key, out var origin);
        Items.Add(
            new EffectiveCodexSettingItem(
                label,
                string.IsNullOrWhiteSpace(value) ? "Inherit / unavailable" : value,
                string.IsNullOrWhiteSpace(origin) ? "Origin unavailable" : origin));
    }

    private void UpdateContext() => ContextPath = NormalizePath(activeWorkspacePath());

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
