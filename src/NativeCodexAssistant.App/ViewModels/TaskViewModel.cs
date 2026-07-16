using System.Collections.ObjectModel;
using System.Windows.Input;
using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class TaskViewModel : ObservableObject
{
    private readonly AsyncRelayCommand submitCommand;
    private readonly AsyncRelayCommand cancelCommand;
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly AsyncRelayCommand steerCommand;
    private CodexThreadService threadService = new();
    private string prompt = string.Empty;
    private string submittedPrompt = string.Empty;
    private string modelOverride = string.Empty;
    private string reasoningEffortOverride = string.Empty;
    private string steeringText = string.Empty;
    private string appServerHealth = "Codex idle";
    private bool isTurnRunning;

    public TaskViewModel(
        Func<Task> submit,
        Func<Task> cancel,
        Func<Task> loadModels,
        Func<Task> steer,
        Func<bool> canCancel,
        Func<bool> canSteer)
    {
        SubmitCommand = submitCommand = new AsyncRelayCommand(submit);
        CancelCommand = cancelCommand = new AsyncRelayCommand(cancel, canCancel);
        LoadModelsCommand = loadModelsCommand = new AsyncRelayCommand(loadModels);
        SteerCommand = steerCommand = new AsyncRelayCommand(steer, canSteer);
    }

    public ObservableCollection<CodexTimelineItem> TimelineItems => threadService.TimelineItems;

    public ObservableCollection<CodexConversationTurn> ConversationTurns => threadService.ConversationTurns;

    public ObservableCollection<string> RawEvents => threadService.RawEvents;

    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<string> ReasoningEffortOptions { get; } =
    [
        string.Empty,
        "none",
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh"
    ];

    public ICommand SubmitCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LoadModelsCommand { get; }
    public ICommand SteerCommand { get; }

    public CodexThreadService ThreadService => threadService;

    public string Prompt
    {
        get => prompt;
        set => SetProperty(ref prompt, value);
    }

    public string SubmittedPrompt
    {
        get => submittedPrompt;
        set
        {
            if (SetProperty(ref submittedPrompt, value))
            {
                OnPropertyChanged(nameof(SubmittedPromptDisplay));
            }
        }
    }

    public string SubmittedPromptDisplay => string.IsNullOrWhiteSpace(SubmittedPrompt)
        ? "No prompt submitted yet"
        : SubmittedPrompt;

    public string ModelOverride
    {
        get => modelOverride;
        set => SetProperty(ref modelOverride, value);
    }

    public string ReasoningEffortOverride
    {
        get => reasoningEffortOverride;
        set => SetProperty(ref reasoningEffortOverride, value);
    }

    public string SteeringText
    {
        get => steeringText;
        set
        {
            if (SetProperty(ref steeringText, value))
            {
                steerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AppServerHealth
    {
        get => appServerHealth;
        set => SetProperty(ref appServerHealth, value);
    }

    public string FinalResponse =>
        string.IsNullOrWhiteSpace(threadService.FinalResponse)
            ? "No final response yet"
            : threadService.FinalResponse;

    public string ComposerActionLabel => ConversationTurns.Count == 0 ? "Run task" : "Send follow-up";

    public bool HasConversation => ConversationTurns.Count > 0;

    public bool IsTurnRunning
    {
        get => isTurnRunning;
        set
        {
            if (SetProperty(ref isTurnRunning, value))
            {
                if (!value)
                {
                    SteeringText = string.Empty;
                }

                RaiseCommandStates();
            }
        }
    }

    public void UseThreadService(CodexThreadService service)
    {
        threadService = service;
        OnPropertyChanged(nameof(TimelineItems));
        OnPropertyChanged(nameof(ConversationTurns));
        OnPropertyChanged(nameof(RawEvents));
        OnPropertyChanged(nameof(FinalResponse));
        OnPropertyChanged(nameof(ComposerActionLabel));
        OnPropertyChanged(nameof(HasConversation));
    }

    public void NotifyResponseChanged()
    {
        OnPropertyChanged(nameof(FinalResponse));
        OnPropertyChanged(nameof(ComposerActionLabel));
        OnPropertyChanged(nameof(HasConversation));
    }

    public void RaiseCommandStates()
    {
        submitCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
        loadModelsCommand.RaiseCanExecuteChanged();
        steerCommand.RaiseCanExecuteChanged();
    }
}
