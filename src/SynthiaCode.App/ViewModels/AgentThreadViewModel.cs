using System.Collections.ObjectModel;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class AgentThreadViewModel(string threadId) : ObservableObject
{
    private string prompt = string.Empty;
    private string? model;
    private string status = "pendingInit";
    private string? statusMessage;
    private string steeringText = string.Empty;
    private string? activeTurnId;
    private bool isBusy;
    private string errorMessage = string.Empty;

    public string ThreadId { get; } = !string.IsNullOrWhiteSpace(threadId)
        ? threadId
        : throw new ArgumentException("Agent thread ID is required.", nameof(threadId));

    public string DisplayName => $"Agent {CreateShortId(ThreadId)}";

    public string Prompt
    {
        get => prompt;
        internal set => SetProperty(ref prompt, value ?? string.Empty);
    }

    public string? Model
    {
        get => model;
        internal set
        {
            if (SetProperty(ref model, value))
            {
                OnPropertyChanged(nameof(HasModel));
            }
        }
    }

    public bool HasModel => !string.IsNullOrWhiteSpace(Model);

    public string Status => status;

    public string StatusLabel => status switch
    {
        "pendingInit" => "Starting",
        "running" => "Running",
        "interrupted" => "Interrupted",
        "completed" => "Completed",
        "errored" => "Failed",
        "shutdown" => "Stopped",
        "notFound" => "Unavailable",
        _ => "Unknown"
    };

    public string? StatusMessage
    {
        get => statusMessage;
        internal set
        {
            if (SetProperty(ref statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsActive => status is "pendingInit" or "running";

    public bool IsDone => !IsActive;

    public ObservableCollection<CodexConversationTurn> Transcript { get; } = [];

    public bool HasTranscript => Transcript.Count > 0;

    public string SteeringText
    {
        get => steeringText;
        set
        {
            if (SetProperty(ref steeringText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSteer));
            }
        }
    }

    public string? ActiveTurnId
    {
        get => activeTurnId;
        private set
        {
            if (SetProperty(ref activeTurnId, value))
            {
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        internal set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(CanSteer));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool CanOpen => !IsBusy;

    public bool CanSteer => IsActive && !IsBusy && !string.IsNullOrWhiteSpace(SteeringText);

    public bool CanStop => IsActive && !IsBusy;

    public string ErrorMessage
    {
        get => errorMessage;
        internal set
        {
            if (SetProperty(ref errorMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    internal void SetStatus(string? value, string? message = null)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "notFound" : value;
        if (!SetProperty(ref status, normalized, nameof(Status)))
        {
            StatusMessage = message;
            return;
        }

        StatusMessage = message;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CanSteer));
        OnPropertyChanged(nameof(CanStop));
    }

    internal void ReplaceTranscript(IReadOnlyList<CodexConversationTurnSnapshot> turns)
    {
        Transcript.Clear();
        foreach (var turn in turns)
        {
            Transcript.Add(CodexConversationTurn.FromSnapshot(turn));
        }

        ActiveTurnId = turns.LastOrDefault(turn => turn.Status == CodexTurnStatus.Running)?.TurnId;
        OnPropertyChanged(nameof(Transcript));
        OnPropertyChanged(nameof(HasTranscript));
    }

    private static string CreateShortId(string value)
    {
        var leaf = value.Trim().TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
        return leaf.Length <= 10 ? leaf : leaf[^10..];
    }
}
