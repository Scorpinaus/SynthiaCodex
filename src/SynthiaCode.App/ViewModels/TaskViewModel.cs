using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Application.Conversations;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AsyncRelayCommand submitCommand;
    private readonly AsyncRelayCommand composerSendCommand;
    private readonly AsyncRelayCommand cancelCommand;
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly AsyncRelayCommand steerCommand;
    private readonly AsyncRelayCommand alternateFollowUpCommand;
    private readonly AsyncRelayCommand toggleDictationCommand;
    private readonly AsyncRelayCommand startCodeReviewCommand;
    private readonly RelayCommand beginPromptEditCommand;
    private readonly RelayCommand cancelPromptEditCommand;
    private readonly AsyncRelayCommand submitPromptEditCommand;
    private readonly AsyncRelayCommand forkConversationCommand;
    private readonly RelayCommand beginQueuedFollowUpEditCommand;
    private readonly RelayCommand cancelQueuedFollowUpEditCommand;
    private readonly AsyncRelayCommand saveQueuedFollowUpEditCommand;
    private readonly AsyncRelayCommand moveQueuedFollowUpUpCommand;
    private readonly AsyncRelayCommand moveQueuedFollowUpDownCommand;
    private readonly AsyncRelayCommand deleteQueuedFollowUpCommand;
    private readonly AsyncRelayCommand sendQueuedFollowUpCommand;
    private readonly RelayCommand openExternalUriCommand;
    private readonly AsyncRelayCommand editGeneratedImageCommand;
    private readonly RelayCommand openOptionsCommand;
    private readonly RelayCommand showOptionsMainCommand;
    private readonly RelayCommand showModelsCommand;
    private readonly RelayCommand showReasoningCommand;
    private readonly RelayCommand removeAttachmentCommand;
    private readonly RelayCommand moveAttachmentLeftCommand;
    private readonly RelayCommand moveAttachmentRightCommand;
    private readonly RelayCommand openFindInChatCommand;
    private readonly RelayCommand closeFindInChatCommand;
    private readonly RelayCommand findNextCommand;
    private readonly RelayCommand findPreviousCommand;
    private readonly IAgentManagementActions agentActions;
    private readonly ISpeechRecognitionService speechRecognitionService;
    private readonly AsyncRelayCommand openAgentCommand;
    private readonly AsyncRelayCommand steerAgentCommand;
    private readonly AsyncRelayCommand stopAgentCommand;
    private readonly RelayCommand closeAgentTranscriptCommand;
    private readonly IGoalManagementActions? goalActions;
    private readonly ICodeReviewActions? codeReviewActions;
    private readonly RelayCommand beginGoalEditCommand;
    private readonly RelayCommand cancelGoalEditCommand;
    private readonly AsyncRelayCommand saveGoalCommand;
    private readonly AsyncRelayCommand toggleGoalStatusCommand;
    private readonly AsyncRelayCommand clearGoalCommand;
    private readonly List<CodexConversationTurn> findMatches = [];
    private readonly Dictionary<string, AgentThreadViewModel> agentsByThread = new(StringComparer.Ordinal);
    private ConversationWorkspaceSnapshot conversation = ConversationWorkspaceSnapshot.Empty;
    private CodexFollowUpQueue followUpQueue = new();
    private readonly ObservableCollection<CodexTimelineItem> timelineItems = [];
    private readonly ObservableCollection<CodexConversationTurn> conversationTurns = [];
    private readonly ObservableCollection<string> rawEvents = [];
    private string prompt = string.Empty;
    private string submittedPrompt = string.Empty;
    private string modelOverride = string.Empty;
    private string reasoningEffortOverride = string.Empty;
    private string steeringText = string.Empty;
    private string appServerHealth = "Codex idle";
    private string accountPlanLabel = string.Empty;
    private string modelCatalogError = string.Empty;
    private CodexModelOption? selectedModel;
    private CodexReasoningOption? selectedReasoning;
    private CodexServiceTierSelection serviceTierSelection;
    private FollowUpBehavior followUpBehavior = FollowUpBehavior.Queue;
    private ComposerOptionsPage optionsPage;
    private bool isTurnRunning;
    private bool isOptionsFlyoutOpen;
    private bool isModelCatalogLoading;
    private bool isModelCatalogStale = true;
    private bool isFindInChatOpen;
    private string findInChatText = string.Empty;
    private int currentFindMatchIndex = -1;
    private AgentThreadViewModel? selectedAgent;
    private bool isAgentTranscriptOpen;
    private bool isRefreshingAgents;
    private bool isDictating;
    private bool isDisposed;
    private string dictationStatusText = string.Empty;
    private CodexThreadGoal? goal;
    private string goalDraft = string.Empty;
    private string goalError = string.Empty;
    private bool isGoalFeatureAvailable;
    private bool isGoalSupported;
    private bool isGoalLoading;
    private bool isGoalEditing;
    private bool isGoalBusy;

    public TaskViewModel(
        ITurnExecutionActions turnActions,
        IFollowUpManagementActions followUpActions,
        IConversationHistoryActions historyActions,
        IComposerSupportActions composerActions,
        IAgentManagementActions agentActions,
        ISpeechRecognitionService? speechRecognitionService = null,
        IGoalManagementActions? goalActions = null,
        ICodeReviewActions? codeReviewActions = null)
    {
        this.agentActions = agentActions;
        this.goalActions = goalActions;
        this.codeReviewActions = codeReviewActions;
        this.speechRecognitionService = speechRecognitionService ?? UnavailableSpeechRecognitionService.Instance;
        this.speechRecognitionService.SpeechRecognized += OnSpeechRecognized;
        this.speechRecognitionService.Stopped += OnSpeechRecognitionStopped;
        SkillSelector = new ComposerSkillSelectorViewModel(
            composerActions.LoadComposerSkillsAsync,
            () => IsTurnRunning ? SteeringText : Prompt,
            value =>
            {
                if (IsTurnRunning)
                {
                    SteeringText = value;
                }
                else
                {
                    Prompt = value;
                }
            });
        SubmitCommand = submitCommand = new AsyncRelayCommand(
            () => RoutesToCodeReview
                ? codeReviewActions!.StartCodeReviewAsync()
                : turnActions.SubmitAsync(),
            () => RoutesToCodeReview
                ? codeReviewActions!.CanStartCodeReview()
                : turnActions.CanSubmitTurn());
        ComposerSendCommand = composerSendCommand = new AsyncRelayCommand(
            () => IsTurnRunning
                ? turnActions.SteerAsync()
                : RoutesToCodeReview
                    ? codeReviewActions!.StartCodeReviewAsync()
                    : turnActions.SubmitAsync(),
            () => IsTurnRunning
                ? turnActions.CanSteerTurn()
                : RoutesToCodeReview
                    ? codeReviewActions!.CanStartCodeReview()
                    : turnActions.CanSubmitTurn());
        CancelCommand = cancelCommand = new AsyncRelayCommand(turnActions.CancelAsync, turnActions.CanCancelTurn);
        LoadModelsCommand = loadModelsCommand = new AsyncRelayCommand(
            composerActions.LoadModelsAsync,
            composerActions.CanLoadModels);
        SteerCommand = steerCommand = new AsyncRelayCommand(turnActions.SteerAsync, turnActions.CanSteerTurn);
        AlternateFollowUpCommand = alternateFollowUpCommand = new AsyncRelayCommand(
            followUpActions.SendAlternateFollowUpAsync,
            turnActions.CanSteerTurn);
        ToggleDictationCommand = toggleDictationCommand = new AsyncRelayCommand(
            ToggleDictationAsync,
            () => IsDictationAvailable && !isDisposed);
        StartCodeReviewCommand = startCodeReviewCommand = new AsyncRelayCommand(
            () => codeReviewActions?.StartCodeReviewAsync() ?? Task.CompletedTask,
            () => codeReviewActions?.CanStartCodeReview() == true);
        beginPromptEditCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is not CodexConversationTurn turn)
                {
                    return;
                }

                foreach (var other in ConversationTurns.Where(item => !ReferenceEquals(item, turn) && item.IsPromptEditing))
                {
                    other.CancelPromptEdit();
                }
                turn.BeginPromptEdit();
                RaisePromptEditCommandStates();
            },
            parameter => parameter is CodexConversationTurn turn &&
                historyActions.CanForkConversation() &&
                !IsTurnRunning &&
                turn.CanEditPrompt &&
                !ConversationTurns.Any(item => item.IsPromptEditing));
        cancelPromptEditCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is CodexConversationTurn turn)
                {
                    turn.CancelPromptEdit();
                    RaisePromptEditCommandStates();
                }
            },
            parameter => parameter is CodexConversationTurn { IsPromptEditing: true });
        submitPromptEditCommand = new AsyncRelayCommand(
            async parameter =>
            {
                if (parameter is not CodexConversationTurn turn || !turn.CanSubmitPromptEdit)
                {
                    return;
                }

                var submitted = await historyActions.EditPromptAsync(turn, turn.EditedPrompt.Trim()).ConfigureAwait(true);
                if (submitted)
                {
                    turn.CancelPromptEdit();
                }
                RaisePromptEditCommandStates();
            },
            parameter => parameter is CodexConversationTurn turn &&
                !IsTurnRunning &&
                turn.IsPromptEditing);
        ForkConversationCommand = forkConversationCommand = new AsyncRelayCommand(
            parameter => parameter is CodexConversationTurn turn
                ? historyActions.ForkConversationAsync(turn.TurnId)
                : Task.CompletedTask,
            parameter => parameter is CodexConversationTurn turn &&
                !IsTurnRunning &&
                turn.Status == CodexTurnStatus.Completed &&
                turn.HasAssistantResponse &&
                turn.CanEditPrompt);
        RemoveAttachmentCommand = removeAttachmentCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is AttachmentReference attachment &&
                    Attachments.FirstOrDefault(item => string.Equals(item.Id, attachment.Id, StringComparison.Ordinal)) is { } stored &&
                    Attachments.Remove(stored))
                {
                    NotifyAttachmentsChanged();
                }
            },
            parameter => parameter is AttachmentReference attachment &&
                Attachments.Any(item => string.Equals(item.Id, attachment.Id, StringComparison.Ordinal)));
        MoveAttachmentLeftCommand = moveAttachmentLeftCommand = new RelayCommand(
            parameter => MoveAttachment(parameter as AttachmentReference, -1),
            parameter => parameter is AttachmentReference attachment && Attachments.IndexOf(attachment) > 0);
        MoveAttachmentRightCommand = moveAttachmentRightCommand = new RelayCommand(
            parameter => MoveAttachment(parameter as AttachmentReference, 1),
            parameter => parameter is AttachmentReference attachment &&
                Attachments.IndexOf(attachment) >= 0 && Attachments.IndexOf(attachment) < Attachments.Count - 1);
        beginQueuedFollowUpEditCommand = new RelayCommand(
            parameter => MutateQueuedFollowUp(parameter, item => followUpQueue.BeginEdit(item.Id)),
            parameter => parameter is QueuedFollowUp { IsStarting: false, IsEditing: false });
        cancelQueuedFollowUpEditCommand = new RelayCommand(
            parameter => MutateQueuedFollowUp(parameter, item => followUpQueue.CancelEdit(item.Id)),
            parameter => parameter is QueuedFollowUp { IsEditing: true });
        saveQueuedFollowUpEditCommand = new AsyncRelayCommand(
            parameter => PersistQueuedMutationAsync(
                parameter,
                item => followUpQueue.CommitEdit(item.Id),
                () => followUpActions.PersistFollowUpQueueAsync(followUpQueue.Snapshot())),
            parameter => parameter is QueuedFollowUp { IsEditing: true, IsStarting: false });
        moveQueuedFollowUpUpCommand = new AsyncRelayCommand(
            parameter => PersistQueuedMutationAsync(
                parameter,
                item => followUpQueue.MoveUp(item.Id),
                () => followUpActions.PersistFollowUpQueueAsync(followUpQueue.Snapshot())),
            parameter => parameter is QueuedFollowUp item && item.IsPending && followUpQueue.IndexOf(item.Id) > 0);
        moveQueuedFollowUpDownCommand = new AsyncRelayCommand(
            parameter => PersistQueuedMutationAsync(
                parameter,
                item => followUpQueue.MoveDown(item.Id),
                () => followUpActions.PersistFollowUpQueueAsync(followUpQueue.Snapshot())),
            parameter => parameter is QueuedFollowUp item && item.IsPending &&
                followUpQueue.IndexOf(item.Id) >= 0 && followUpQueue.IndexOf(item.Id) < followUpQueue.Items.Count - 1);
        deleteQueuedFollowUpCommand = new AsyncRelayCommand(
            parameter => PersistQueuedMutationAsync(
                parameter,
                item => followUpQueue.Remove(item.Id),
                () => followUpActions.PersistFollowUpQueueAsync(followUpQueue.Snapshot())),
            parameter => parameter is QueuedFollowUp { IsStarting: false });
        sendQueuedFollowUpCommand = new AsyncRelayCommand(
            parameter => parameter is QueuedFollowUp item
                ? followUpActions.SendQueuedFollowUpAsync(item.Id)
                : Task.CompletedTask,
            parameter => parameter is QueuedFollowUp { IsStarting: false });
        OpenExternalUriCommand = openExternalUriCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is Uri uri && ExternalUriPolicy.IsSupported(uri))
                {
                    followUpActions.OpenExternalUri(uri);
                }
                else if (parameter is Uri localUri &&
                         LocalImageResourcePolicy.IsSupported(localUri, out var imagePath))
                {
                    composerActions.ShowImagePreview(imagePath);
                }
            },
            parameter => parameter is Uri uri &&
                (ExternalUriPolicy.IsSupported(uri) ||
                 LocalImageResourcePolicy.IsSupported(uri, out _)));
        EditGeneratedImageCommand = editGeneratedImageCommand = new AsyncRelayCommand(
            parameter => parameter is string path
                ? composerActions.EditGeneratedImageAsync(path)
                : Task.CompletedTask,
            parameter => parameter is string path &&
                !IsTurnRunning &&
                LocalImageResourcePolicy.TryCreateSupportedUri(path, out _, out _));
        OpenOptionsCommand = openOptionsCommand = new RelayCommand(
            () =>
            {
                OptionsPage = ComposerOptionsPage.Main;
                IsOptionsFlyoutOpen = true;
                if (IsModelCatalogStale && !IsModelCatalogLoading)
                {
                    loadModelsCommand.Execute(null);
                }
            },
            () => !IsTurnRunning);
        ShowOptionsMainCommand = showOptionsMainCommand = new RelayCommand(
            () => OptionsPage = ComposerOptionsPage.Main);
        ShowModelsCommand = showModelsCommand = new RelayCommand(
            () => OptionsPage = ComposerOptionsPage.Models,
            () => ModelCatalog.Count > 0);
        ShowReasoningCommand = showReasoningCommand = new RelayCommand(
            () => OptionsPage = ComposerOptionsPage.Reasoning,
            () => ReasoningOptions.Count > 0);
        OpenFindInChatCommand = openFindInChatCommand = new RelayCommand(
            () => IsFindInChatOpen = true);
        CloseFindInChatCommand = closeFindInChatCommand = new RelayCommand(CloseFindInChat);
        FindNextCommand = findNextCommand = new RelayCommand(
            () => MoveFindMatch(1),
            () => findMatches.Count > 0);
        FindPreviousCommand = findPreviousCommand = new RelayCommand(
            () => MoveFindMatch(-1),
            () => findMatches.Count > 0);
        OpenAgentCommand = openAgentCommand = new AsyncRelayCommand(OpenAgentAsync, CanOpenAgent);
        SteerAgentCommand = steerAgentCommand = new AsyncRelayCommand(SteerAgentAsync, CanSteerAgent);
        StopAgentCommand = stopAgentCommand = new AsyncRelayCommand(StopAgentAsync, CanStopAgent);
        CloseAgentTranscriptCommand = closeAgentTranscriptCommand = new RelayCommand(CloseAgentTranscript);
        BeginGoalEditCommand = beginGoalEditCommand = new RelayCommand(BeginGoalEdit, CanBeginGoalEdit);
        CancelGoalEditCommand = cancelGoalEditCommand = new RelayCommand(CancelGoalEdit, () => IsGoalEditing && !IsGoalBusy);
        SaveGoalCommand = saveGoalCommand = new AsyncRelayCommand(SaveGoalAsync, CanSaveGoal);
        ToggleGoalStatusCommand = toggleGoalStatusCommand = new AsyncRelayCommand(ToggleGoalStatusAsync, CanToggleGoalStatus);
        ClearGoalCommand = clearGoalCommand = new AsyncRelayCommand(ClearGoalAsync, CanClearGoal);
    }

    public ObservableCollection<CodexTimelineItem> TimelineItems => timelineItems;

    public ObservableCollection<CodexConversationTurn> ConversationTurns => conversationTurns;

    public ObservableCollection<AgentThreadViewModel> ActiveAgents { get; } = [];

    public ObservableCollection<AgentThreadViewModel> DoneAgents { get; } = [];

    public bool HasAgents => ActiveAgents.Count > 0 || DoneAgents.Count > 0;

    public bool HasActiveAgents => ActiveAgents.Count > 0;

    public bool HasDoneAgents => DoneAgents.Count > 0;

    public AgentThreadViewModel? SelectedAgent
    {
        get => selectedAgent;
        private set => SetProperty(ref selectedAgent, value);
    }

    public bool IsAgentTranscriptOpen
    {
        get => isAgentTranscriptOpen;
        private set => SetProperty(ref isAgentTranscriptOpen, value);
    }

    public string? ConversationThreadId => conversation.ThreadId;

    public ObservableCollection<string> RawEvents => rawEvents;

    public ObservableCollection<QueuedFollowUp> QueuedFollowUps => followUpQueue.Items;

    public ObservableCollection<AttachmentReference> Attachments { get; } = [];

    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<CodexModelOption> ModelCatalog { get; } = [];

    public ObservableCollection<CodexReasoningOption> ReasoningOptions { get; } = [];

    public ObservableCollection<string> ReasoningEffortOptions { get; } = [];

    public ComposerSkillSelectorViewModel SkillSelector { get; }

    public IReadOnlyList<FollowUpBehavior> FollowUpBehaviorOptions { get; } =
        [FollowUpBehavior.Queue, FollowUpBehavior.Steer];

    public ICommand SubmitCommand { get; }
    public ICommand ComposerSendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LoadModelsCommand { get; }
    public ICommand SteerCommand { get; }
    public ICommand AlternateFollowUpCommand { get; }
    public ICommand ToggleDictationCommand { get; }
    public ICommand StartCodeReviewCommand { get; }
    public ICommand BeginPromptEditCommand => beginPromptEditCommand;
    public ICommand CancelPromptEditCommand => cancelPromptEditCommand;
    public ICommand SubmitPromptEditCommand => submitPromptEditCommand;
    public ICommand ForkConversationCommand { get; }
    public ICommand BeginQueuedFollowUpEditCommand => beginQueuedFollowUpEditCommand;
    public ICommand CancelQueuedFollowUpEditCommand => cancelQueuedFollowUpEditCommand;
    public ICommand SaveQueuedFollowUpEditCommand => saveQueuedFollowUpEditCommand;
    public ICommand MoveQueuedFollowUpUpCommand => moveQueuedFollowUpUpCommand;
    public ICommand MoveQueuedFollowUpDownCommand => moveQueuedFollowUpDownCommand;
    public ICommand DeleteQueuedFollowUpCommand => deleteQueuedFollowUpCommand;
    public ICommand SendQueuedFollowUpCommand => sendQueuedFollowUpCommand;
    public ICommand OpenExternalUriCommand { get; }
    public ICommand EditGeneratedImageCommand { get; }
    public ICommand OpenOptionsCommand { get; }
    public ICommand ShowOptionsMainCommand { get; }
    public ICommand ShowModelsCommand { get; }
    public ICommand ShowReasoningCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand MoveAttachmentLeftCommand { get; }
    public ICommand MoveAttachmentRightCommand { get; }
    public ICommand OpenFindInChatCommand { get; }
    public ICommand CloseFindInChatCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand FindPreviousCommand { get; }
    public ICommand OpenAgentCommand { get; }
    public ICommand SteerAgentCommand { get; }
    public ICommand StopAgentCommand { get; }
    public ICommand CloseAgentTranscriptCommand { get; }
    public ICommand BeginGoalEditCommand { get; }
    public ICommand CancelGoalEditCommand { get; }
    public ICommand SaveGoalCommand { get; }
    public ICommand ToggleGoalStatusCommand { get; }
    public ICommand ClearGoalCommand { get; }

    public bool IsDictationAvailable => speechRecognitionService.Availability.IsAvailable;

    public bool IsDictating
    {
        get => isDictating;
        private set
        {
            if (SetProperty(ref isDictating, value))
            {
                OnPropertyChanged(nameof(DictationToolTip));
                OnPropertyChanged(nameof(DictationAutomationName));
            }
        }
    }

    public string DictationStatusText
    {
        get => dictationStatusText;
        private set
        {
            if (SetProperty(ref dictationStatusText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DictationToolTip));
            }
        }
    }

    public string DictationToolTip => !IsDictationAvailable
        ? speechRecognitionService.Availability.Message
        : IsDictating
            ? "Stop dictation"
            : DictationStatusText.StartsWith("Dictation unavailable:", StringComparison.Ordinal)
                ? DictationStatusText
                : "Start dictation";

    public string DictationAutomationName => IsDictating ? "Stop dictation" : "Start dictation";

    public CodexThreadGoal? Goal => goal;

    public bool IsGoalFeatureAvailable
    {
        get => isGoalFeatureAvailable;
        private set => SetProperty(ref isGoalFeatureAvailable, value);
    }

    public bool IsGoalSupported
    {
        get => isGoalSupported;
        private set
        {
            if (SetProperty(ref isGoalSupported, value))
            {
                RaiseGoalCommandStates();
            }
        }
    }

    public bool IsGoalLoading
    {
        get => isGoalLoading;
        private set
        {
            if (SetProperty(ref isGoalLoading, value))
            {
                RaiseGoalCommandStates();
            }
        }
    }

    public bool IsGoalEditing
    {
        get => isGoalEditing;
        private set
        {
            if (SetProperty(ref isGoalEditing, value))
            {
                OnPropertyChanged(nameof(GoalEditorValidationMessage));
                RaiseGoalCommandStates();
            }
        }
    }

    public bool IsGoalBusy
    {
        get => isGoalBusy;
        private set
        {
            if (SetProperty(ref isGoalBusy, value))
            {
                RaiseGoalCommandStates();
            }
        }
    }

    public string GoalDraft
    {
        get => goalDraft;
        set
        {
            if (SetProperty(ref goalDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(GoalEditorValidationMessage));
                OnPropertyChanged(nameof(GoalCharacterCount));
                saveGoalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string GoalError
    {
        get => goalError;
        private set
        {
            if (SetProperty(ref goalError, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasGoalError));
            }
        }
    }

    public bool HasGoal => Goal is not null;

    public bool HasGoalError => !string.IsNullOrWhiteSpace(GoalError);

    public string GoalObjective => Goal?.Objective ?? string.Empty;

    public string GoalStatusLabel => Goal?.Status.ToDisplayName() ?? string.Empty;

    public string GoalStatusAutomationName => HasGoal ? $"Goal status: {GoalStatusLabel}" : "No goal set";

    public string GoalToggleActionLabel => Goal?.Status == CodexThreadGoalStatus.Active ? "Pause" : "Resume";

    public string GoalUsageSummary
    {
        get
        {
            if (Goal is null)
            {
                return string.Empty;
            }

            var tokens = Goal.TokenBudget is > 0
                ? $"{FormatCompactTokenCount(Goal.TokensUsed)}/{FormatCompactTokenCount(Goal.TokenBudget.Value)} tokens"
                : $"{FormatCompactTokenCount(Goal.TokensUsed)} tokens";
            return $"{tokens} | {FormatGoalDuration(Goal.TimeUsedSeconds)}";
        }
    }

    public string GoalCharacterCount => $"{GoalDraft.Length:N0}/4,000";

    public string GoalEditorValidationMessage => !IsGoalEditing
        ? string.Empty
        : string.IsNullOrWhiteSpace(GoalDraft)
            ? "Enter a goal objective."
            : GoalDraft.Length > 4_000
                ? "Goal objectives must be 4,000 characters or fewer."
                : string.Empty;

    public void ResetGoalContext(bool isCodexThread)
    {
        goal = null;
        goalDraft = string.Empty;
        goalError = string.Empty;
        isGoalLoading = false;
        isGoalEditing = false;
        isGoalBusy = false;
        isGoalFeatureAvailable = isCodexThread;
        isGoalSupported = isCodexThread;
        RaiseGoalPropertiesChanged();
    }

    public void SetGoalLoading()
    {
        if (!IsGoalFeatureAvailable)
        {
            return;
        }

        GoalError = string.Empty;
        IsGoalLoading = true;
    }

    public void ApplyGoal(CodexThreadGoal? value)
    {
        goal = value;
        isGoalSupported = true;
        if (!IsGoalEditing)
        {
            goalDraft = value?.Objective ?? string.Empty;
        }
        GoalError = string.Empty;
        IsGoalLoading = false;
        RaiseGoalPropertiesChanged();
    }

    public void SetGoalLoadError(string message)
    {
        IsGoalLoading = false;
        GoalError = message;
        RaiseGoalCommandStates();
    }

    public void SetGoalUnsupported(string message)
    {
        IsGoalLoading = false;
        IsGoalSupported = false;
        GoalError = message;
    }

    private void BeginGoalEdit()
    {
        GoalDraft = Goal?.Objective ?? string.Empty;
        GoalError = string.Empty;
        IsGoalEditing = true;
    }

    private bool CanBeginGoalEdit() =>
        IsGoalFeatureAvailable &&
        IsGoalSupported &&
        goalActions?.CanManageGoal() == true &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        !IsGoalEditing;

    private void CancelGoalEdit()
    {
        GoalDraft = Goal?.Objective ?? string.Empty;
        GoalError = string.Empty;
        IsGoalEditing = false;
    }

    private bool CanSaveGoal() =>
        IsGoalEditing &&
        IsGoalSupported &&
        goalActions?.CanManageGoal() == true &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        string.IsNullOrEmpty(GoalEditorValidationMessage) &&
        !string.Equals(GoalDraft.Trim(), Goal?.Objective, StringComparison.Ordinal);

    private async Task SaveGoalAsync()
    {
        if (!CanSaveGoal() || goalActions is null)
        {
            return;
        }

        var objective = GoalDraft.Trim();
        var isNewGoal = Goal is null;
        var contextThreadId = ConversationThreadId;
        IsGoalBusy = true;
        GoalError = string.Empty;
        try
        {
            var saved = await goalActions.SetGoalAsync(objective).ConfigureAwait(true);
            if (!string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                return;
            }
            ApplyGoal(saved);
            IsGoalEditing = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                GoalError = $"Could not save the goal: {exception.Message}";
            }
            return;
        }
        finally
        {
            if (string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                IsGoalBusy = false;
            }
        }

        if (!isNewGoal ||
            string.IsNullOrWhiteSpace(contextThreadId) ||
            !string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await goalActions.StartGoalWorkAsync(contextThreadId, objective).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GoalError = $"The goal was saved, but work could not start: {exception.Message}";
        }
    }

    private bool CanToggleGoalStatus() =>
        goalActions?.CanManageGoal() == true &&
        IsGoalSupported &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        Goal?.Status is CodexThreadGoalStatus.Active or CodexThreadGoalStatus.Paused;

    private async Task ToggleGoalStatusAsync()
    {
        if (!CanToggleGoalStatus() || goalActions is null || Goal is null)
        {
            return;
        }

        var status = Goal.Status == CodexThreadGoalStatus.Active
            ? CodexThreadGoalStatus.Paused
            : CodexThreadGoalStatus.Active;
        var contextThreadId = ConversationThreadId;
        IsGoalBusy = true;
        GoalError = string.Empty;
        try
        {
            var updated = await goalActions.SetGoalStatusAsync(status).ConfigureAwait(true);
            if (!string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                return;
            }
            ApplyGoal(updated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                GoalError = $"Could not {GoalToggleActionLabel.ToLowerInvariant()} the goal: {exception.Message}";
            }
        }
        finally
        {
            if (string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                IsGoalBusy = false;
            }
        }
    }

    private bool CanClearGoal() =>
        goalActions?.CanManageGoal() == true &&
        IsGoalSupported &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        HasGoal;

    private async Task ClearGoalAsync()
    {
        if (!CanClearGoal() || goalActions is null)
        {
            return;
        }

        var contextThreadId = ConversationThreadId;
        IsGoalBusy = true;
        GoalError = string.Empty;
        try
        {
            await goalActions.ClearGoalAsync().ConfigureAwait(true);
            if (!string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                return;
            }
            ApplyGoal(null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                GoalError = $"Could not clear the goal: {exception.Message}";
            }
        }
        finally
        {
            if (string.Equals(contextThreadId, ConversationThreadId, StringComparison.Ordinal))
            {
                IsGoalBusy = false;
            }
        }
    }

    private void RaiseGoalPropertiesChanged()
    {
        OnPropertyChanged(nameof(Goal));
        OnPropertyChanged(nameof(IsGoalFeatureAvailable));
        OnPropertyChanged(nameof(IsGoalSupported));
        OnPropertyChanged(nameof(IsGoalLoading));
        OnPropertyChanged(nameof(IsGoalEditing));
        OnPropertyChanged(nameof(IsGoalBusy));
        OnPropertyChanged(nameof(GoalDraft));
        OnPropertyChanged(nameof(GoalCharacterCount));
        OnPropertyChanged(nameof(GoalEditorValidationMessage));
        OnPropertyChanged(nameof(GoalError));
        OnPropertyChanged(nameof(HasGoalError));
        OnPropertyChanged(nameof(HasGoal));
        OnPropertyChanged(nameof(GoalObjective));
        OnPropertyChanged(nameof(GoalStatusLabel));
        OnPropertyChanged(nameof(GoalStatusAutomationName));
        OnPropertyChanged(nameof(GoalToggleActionLabel));
        OnPropertyChanged(nameof(GoalUsageSummary));
        RaiseGoalCommandStates();
    }

    private void RaiseGoalCommandStates()
    {
        beginGoalEditCommand.RaiseCanExecuteChanged();
        cancelGoalEditCommand.RaiseCanExecuteChanged();
        saveGoalCommand.RaiseCanExecuteChanged();
        toggleGoalStatusCommand.RaiseCanExecuteChanged();
        clearGoalCommand.RaiseCanExecuteChanged();
    }

    private static string FormatGoalDuration(long totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (duration.TotalHours >= 1)
        {
            return $"{(long)duration.TotalHours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1
            ? $"{(long)duration.TotalMinutes}m"
            : $"{duration.Seconds}s";
    }

    public bool HasAttachments => Attachments.Count > 0;

    public bool CanSubmitAttachments => !Attachments.Any(attachment => attachment.IsImage) || SelectedModel?.SupportsImageInput != false;

    public string AttachmentValidationMessage => CanSubmitAttachments
        ? string.Empty
        : $"{SelectedModel?.DisplayName ?? "The selected model"} does not accept image input. Remove the images or choose an image-capable model.";

    public void AddAttachment(AttachmentReference attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (Attachments.Any(existing => IsDuplicate(existing, attachment)))
        {
            return;
        }
        if (Attachments.Count >= AttachmentLimits.MaximumAttachmentsPerInput)
        {
            throw new InvalidOperationException($"A prompt can contain at most {AttachmentLimits.MaximumAttachmentsPerInput} attachments.");
        }
        if (attachment.IsImage && Attachments.Count(item => item.IsImage) >= AttachmentLimits.MaximumImagesPerInput)
        {
            throw new InvalidOperationException($"A prompt can contain at most {AttachmentLimits.MaximumImagesPerInput} images.");
        }
        if (attachment.IsFolder && Attachments.Count(item => item.IsFolder) >= AttachmentLimits.MaximumFoldersPerInput)
        {
            throw new InvalidOperationException($"A prompt can contain at most {AttachmentLimits.MaximumFoldersPerInput} folders.");
        }
        var managedBytes = Attachments
            .Where(item => item.SourceKind == AttachmentSourceKind.ManagedCopy)
            .Sum(item => item.ByteLength);
        if (attachment.SourceKind == AttachmentSourceKind.ManagedCopy &&
            managedBytes + attachment.ByteLength > AttachmentLimits.MaximumBytesPerInput)
        {
            throw new InvalidOperationException($"Managed prompt attachments cannot exceed {AttachmentLimits.MaximumBytesPerInput / (1024 * 1024)} MiB in total.");
        }
        Attachments.Add(attachment.Clone());
        NotifyAttachmentsChanged();
    }

    private static bool IsDuplicate(AttachmentReference left, AttachmentReference right)
    {
        if (left.SourceKind == AttachmentSourceKind.WorkspaceReference &&
            right.SourceKind == AttachmentSourceKind.WorkspaceReference)
        {
            return left.Kind == right.Kind &&
                string.Equals(left.WorkspaceRelativePath, right.WorkspaceRelativePath, StringComparison.OrdinalIgnoreCase);
        }

        return left.Kind == right.Kind &&
            !string.IsNullOrWhiteSpace(left.ContentSha256) &&
            string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void ReplaceAttachments(IEnumerable<AttachmentReference>? attachments)
    {
        Attachments.Clear();
        foreach (var attachment in attachments ?? [])
        {
            Attachments.Add(attachment.Clone());
        }
        NotifyAttachmentsChanged();
    }

    public void ClearAttachments()
    {
        if (Attachments.Count == 0)
        {
            return;
        }
        Attachments.Clear();
        NotifyAttachmentsChanged();
    }

    public string Prompt
    {
        get => prompt;
        set
        {
            if (SetProperty(ref prompt, value ?? string.Empty))
            {
                SkillSelector.ReconcileText(prompt);
                submitCommand.RaiseCanExecuteChanged();
                composerSendCommand.RaiseCanExecuteChanged();
            }
        }
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
        set
        {
            if (SetProperty(ref modelOverride, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public string ReasoningEffortOverride
    {
        get => reasoningEffortOverride;
        set
        {
            if (SetProperty(ref reasoningEffortOverride, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public CodexModelOption? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (!SetProperty(ref selectedModel, value))
            {
                return;
            }

            if (value is not null)
            {
                ModelOverride = value.Model;
            }
            RebuildReasoningOptions();
            ReconcileFastAvailability();
            OnPropertyChanged(nameof(ModelSelectionSummary));
            OnPropertyChanged(nameof(FastModeDescription));
            OnPropertyChanged(nameof(CanSubmitAttachments));
            OnPropertyChanged(nameof(AttachmentValidationMessage));
            RaiseCommandStates();
        }
    }

    public CodexReasoningOption? SelectedReasoning
    {
        get => selectedReasoning;
        set
        {
            if (SetProperty(ref selectedReasoning, value))
            {
                ReasoningEffortOverride = value?.ProtocolValue ?? string.Empty;
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public CodexServiceTierSelection ServiceTierSelection
    {
        get => serviceTierSelection;
        set
        {
            if (SetProperty(ref serviceTierSelection, value))
            {
                OnPropertyChanged(nameof(IsFastModeEnabled));
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public bool IsFastModeEnabled
    {
        get => ServiceTierSelection == CodexServiceTierSelection.Fast;
        set
        {
            if (value && !IsFastModeAvailable)
            {
                return;
            }
            ServiceTierSelection = value
                ? CodexServiceTierSelection.Fast
                : CodexServiceTierSelection.Standard;
        }
    }

    public bool IsFastModeAvailable => SelectedModel?.SupportsFastMode == true;

    public string FastModeDescription => SelectedModel?.FastServiceTier?.Description
        ?? (SelectedModel is null
            ? "Load models to check Fast availability."
            : IsFastModeAvailable
                ? "Faster responses at higher credit use."
                : $"Fast is not available for {SelectedModel.DisplayName} on this account.");

    public string ModelSelectionSummary
    {
        get
        {
            var model = SelectedModel?.DisplayName
                ?? (string.IsNullOrWhiteSpace(ModelOverride) ? "Default model" : ModelOverride);
            var reasoning = SelectedReasoning?.DisplayName
                ?? ParseReasoningEffort(ReasoningEffortOverride)?.ToDisplayName();
            var values = new List<string> { model };
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                values.Add(reasoning);
            }
            if (IsFastModeEnabled)
            {
                values.Add("Fast");
            }
            return string.Join(" · ", values);
        }
    }

    public string AccountPlanLabel
    {
        get => accountPlanLabel;
        private set
        {
            if (SetProperty(ref accountPlanLabel, value))
            {
                OnPropertyChanged(nameof(HasAccountPlanLabel));
            }
        }
    }

    public bool HasAccountPlanLabel => !string.IsNullOrWhiteSpace(AccountPlanLabel);

    public bool IsModelCatalogLoading
    {
        get => isModelCatalogLoading;
        private set => SetProperty(ref isModelCatalogLoading, value);
    }

    public bool IsModelCatalogStale
    {
        get => isModelCatalogStale;
        private set => SetProperty(ref isModelCatalogStale, value);
    }

    public string ModelCatalogError
    {
        get => modelCatalogError;
        private set
        {
            if (SetProperty(ref modelCatalogError, value))
            {
                OnPropertyChanged(nameof(HasModelCatalogError));
            }
        }
    }

    public bool HasModelCatalogError => !string.IsNullOrWhiteSpace(ModelCatalogError);

    public bool IsOptionsFlyoutOpen
    {
        get => isOptionsFlyoutOpen;
        set => SetProperty(ref isOptionsFlyoutOpen, value);
    }

    public ComposerOptionsPage OptionsPage
    {
        get => optionsPage;
        set
        {
            if (SetProperty(ref optionsPage, value))
            {
                OnPropertyChanged(nameof(IsOptionsMainPage));
                OnPropertyChanged(nameof(IsOptionsModelPage));
                OnPropertyChanged(nameof(IsOptionsReasoningPage));
            }
        }
    }

    public bool IsOptionsMainPage => OptionsPage == ComposerOptionsPage.Main;

    public bool IsOptionsModelPage => OptionsPage == ComposerOptionsPage.Models;

    public bool IsOptionsReasoningPage => OptionsPage == ComposerOptionsPage.Reasoning;

    public string SteeringText
    {
        get => steeringText;
        set
        {
            if (SetProperty(ref steeringText, value))
            {
                SkillSelector.ReconcileText(steeringText);
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
        string.IsNullOrWhiteSpace(conversation.FinalResponse)
            ? "No final response yet"
            : conversation.FinalResponse;

    private int ContextUsedPercent => conversation.ContextWindowTokens <= 0 ? 0 : Math.Clamp(
        (int)Math.Round(conversation.ContextTokensUsed * 100d / conversation.ContextWindowTokens, MidpointRounding.AwayFromZero), 0, 100);

    public string ContextWindowIndicator => conversation.ContextWindowTokens > 0
        ? $"{ContextUsedPercent}%"
        : "—%";

    public string ContextWindowToolTip => conversation.ContextWindowTokens > 0
        ? string.Join(
            Environment.NewLine,
            "Context window",
            $"{ContextUsedPercent}% used, {100 - ContextUsedPercent}% remaining",
            $"{FormatCompactTokenCount(conversation.ContextTokensUsed)}/{FormatCompactTokenCount(conversation.ContextWindowTokens)} tokens used",
            $"Compactions: {conversation.ContextCompactionCount}")
        : string.Join(
            Environment.NewLine,
            "Context window",
            "Usage unavailable",
            $"Compactions: {conversation.ContextCompactionCount}");

    public string ComposerActionLabel => IsTurnRunning
        ? FollowUpBehavior == FollowUpBehavior.Queue ? "Queue follow-up" : "Steer task"
        : ConversationTurns.Count == 0 ? "Run task" : "Send follow-up";

    public string AlternateFollowUpActionLabel => FollowUpBehavior == FollowUpBehavior.Queue
        ? "Steer current turn"
        : "Queue for next turn";

    public FollowUpBehavior FollowUpBehavior
    {
        get => followUpBehavior;
        set
        {
            if (SetProperty(ref followUpBehavior, value))
            {
                OnPropertyChanged(nameof(ComposerActionLabel));
                OnPropertyChanged(nameof(AlternateFollowUpActionLabel));
            }
        }
    }

    public bool HasConversation => ConversationTurns.Count > 0;

    public bool IsFindInChatOpen
    {
        get => isFindInChatOpen;
        private set => SetProperty(ref isFindInChatOpen, value);
    }

    public string FindInChatText
    {
        get => findInChatText;
        set
        {
            if (SetProperty(ref findInChatText, value ?? string.Empty))
            {
                RefreshFindInChatMatches();
            }
        }
    }

    public int FindInChatMatchCount => findMatches.Count;

    public int CurrentFindInChatMatchNumber =>
        currentFindMatchIndex >= 0 && currentFindMatchIndex < findMatches.Count
            ? currentFindMatchIndex + 1
            : 0;

    public CodexConversationTurn? CurrentFindInChatTurn =>
        currentFindMatchIndex >= 0 && currentFindMatchIndex < findMatches.Count
            ? findMatches[currentFindMatchIndex]
            : null;

    public string FindInChatSummary => FindInChatMatchCount == 0
        ? "0 results"
        : $"{CurrentFindInChatMatchNumber} of {FindInChatMatchCount}";

    public bool IsTurnRunning
    {
        get => isTurnRunning;
        set
        {
            if (SetProperty(ref isTurnRunning, value))
            {
                OnPropertyChanged(nameof(ComposerActionLabel));
                if (!value)
                {
                    SteeringText = string.Empty;
                }
                else
                {
                    IsOptionsFlyoutOpen = false;
                }

                RaiseCommandStates();
            }
        }
    }

    public void ApplyConversationSnapshot(ConversationWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var conversationChanged = !string.Equals(
            conversation.ThreadId,
            snapshot.ThreadId,
            StringComparison.Ordinal);
        ClearFindMatchFlags();
        conversation = snapshot;
        timelineItems.Clear();
        foreach (var item in snapshot.TimelineItems)
        {
            timelineItems.Add(item with { });
        }
        rawEvents.Clear();
        foreach (var item in snapshot.RawEvents)
        {
            rawEvents.Add(item);
        }
        conversationTurns.Clear();
        foreach (var item in snapshot.ConversationTurns)
        {
            conversationTurns.Add(CodexConversationTurn.FromSnapshot(item));
        }
        followUpQueue = new CodexFollowUpQueue();
        followUpQueue.Restore(snapshot.QueuedFollowUps);
        OnPropertyChanged(nameof(TimelineItems));
        OnPropertyChanged(nameof(ConversationTurns));
        OnPropertyChanged(nameof(RawEvents));
        OnPropertyChanged(nameof(FinalResponse));
        OnPropertyChanged(nameof(ComposerActionLabel));
        OnPropertyChanged(nameof(HasConversation));
        if (conversationChanged)
        {
            OnPropertyChanged(nameof(ConversationThreadId));
        }
        OnPropertyChanged(nameof(ContextWindowIndicator));
        OnPropertyChanged(nameof(ContextWindowToolTip));
        OnPropertyChanged(nameof(QueuedFollowUps));
        OnPropertyChanged(nameof(HasQueuedFollowUps));
        RefreshAgents(snapshot, conversationChanged);
        RefreshFindInChatMatches();
    }

    private void RefreshAgents(ConversationWorkspaceSnapshot snapshot, bool conversationChanged)
    {
        isRefreshingAgents = true;
        try
        {
            if (conversationChanged)
            {
                agentsByThread.Clear();
                CloseAgentTranscript();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var activity in snapshot.ConversationTurns
                         .SelectMany(turn => turn.Activity)
                         .Where(item => item.Kind == CodexTimelineItemKind.Collaboration))
            {
                foreach (var threadId in activity.CollaborationReceiverThreadIds)
                {
                    var agent = GetOrCreateAgent(threadId);
                    seen.Add(threadId);
                    if (string.Equals(activity.CollaborationTool, "spawnAgent", StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(activity.CollaborationPrompt))
                        {
                            agent.Prompt = activity.CollaborationPrompt;
                        }

                        if (!string.IsNullOrWhiteSpace(activity.CollaborationModel))
                        {
                            agent.Model = activity.CollaborationModel;
                        }
                    }
                }

                foreach (var state in activity.CollaborationAgentStates)
                {
                    var agent = GetOrCreateAgent(state.ThreadId);
                    seen.Add(state.ThreadId);
                    agent.SetStatus(state.Status, state.Message);
                }
            }

            foreach (var threadId in agentsByThread.Keys.Where(threadId => !seen.Contains(threadId)).ToArray())
            {
                if (ReferenceEquals(SelectedAgent, agentsByThread[threadId]))
                {
                    CloseAgentTranscript();
                }

                agentsByThread.Remove(threadId);
            }
        }
        finally
        {
            isRefreshingAgents = false;
        }

        RefreshAgentGroups();
    }

    private AgentThreadViewModel GetOrCreateAgent(string threadId)
    {
        if (agentsByThread.TryGetValue(threadId, out var existing))
        {
            return existing;
        }

        var created = new AgentThreadViewModel(threadId);
        created.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentThreadViewModel.IsActive) && !isRefreshingAgents)
            {
                RefreshAgentGroups();
            }

            RaiseAgentCommandStates();
        };
        agentsByThread.Add(threadId, created);
        return created;
    }

    private void RefreshAgentGroups()
    {
        ActiveAgents.Clear();
        DoneAgents.Clear();
        foreach (var agent in agentsByThread.Values)
        {
            (agent.IsActive ? ActiveAgents : DoneAgents).Add(agent);
        }

        OnPropertyChanged(nameof(ActiveAgents));
        OnPropertyChanged(nameof(DoneAgents));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(HasActiveAgents));
        OnPropertyChanged(nameof(HasDoneAgents));
        RaiseAgentCommandStates();
    }

    private bool CanOpenAgent(object? parameter) =>
        parameter is AgentThreadViewModel agent && agent.CanOpen;

    private async Task OpenAgentAsync(object? parameter)
    {
        if (parameter is not AgentThreadViewModel agent)
        {
            return;
        }

        SelectedAgent = agent;
        IsAgentTranscriptOpen = true;
        await LoadAgentTranscriptAsync(agent).ConfigureAwait(true);
    }

    private bool CanSteerAgent(object? parameter) =>
        parameter is AgentThreadViewModel agent && agent.CanSteer;

    private async Task SteerAgentAsync(object? parameter)
    {
        if (parameter is not AgentThreadViewModel agent)
        {
            return;
        }

        var message = agent.SteeringText.Trim();
        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            await LoadAgentTranscriptAsync(agent).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            agent.ErrorMessage = "The agent no longer has a running turn to steer.";
            return;
        }

        agent.IsBusy = true;
        agent.ErrorMessage = string.Empty;
        try
        {
            await agentActions.SteerAgentAsync(agent.ThreadId, agent.ActiveTurnId, message).ConfigureAwait(true);
            agent.SteeringText = string.Empty;
        }
        catch (Exception exception)
        {
            agent.ErrorMessage = $"Could not steer agent: {exception.Message}";
        }
        finally
        {
            agent.IsBusy = false;
        }
    }

    private bool CanStopAgent(object? parameter) =>
        parameter is AgentThreadViewModel agent && agent.CanStop;

    private async Task StopAgentAsync(object? parameter)
    {
        if (parameter is not AgentThreadViewModel agent)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            await LoadAgentTranscriptAsync(agent).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            agent.ErrorMessage = "The agent no longer has a running turn to stop.";
            return;
        }

        agent.IsBusy = true;
        agent.ErrorMessage = string.Empty;
        try
        {
            await agentActions.StopAgentAsync(agent.ThreadId, agent.ActiveTurnId).ConfigureAwait(true);
            agent.SetStatus("interrupted", "Stopped by user.");
        }
        catch (Exception exception)
        {
            agent.ErrorMessage = $"Could not stop agent: {exception.Message}";
        }
        finally
        {
            agent.IsBusy = false;
        }
    }

    private async Task LoadAgentTranscriptAsync(AgentThreadViewModel agent)
    {
        agent.IsBusy = true;
        agent.ErrorMessage = string.Empty;
        try
        {
            var result = await agentActions.ReadAgentThreadAsync(agent.ThreadId).ConfigureAwait(true);
            agent.ReplaceTranscript(result.Turns);
        }
        catch (Exception exception)
        {
            agent.ErrorMessage = $"Could not open agent transcript: {exception.Message}";
        }
        finally
        {
            agent.IsBusy = false;
        }
    }

    private void CloseAgentTranscript()
    {
        IsAgentTranscriptOpen = false;
        SelectedAgent = null;
    }

    public bool HasQueuedFollowUps => QueuedFollowUps.Count > 0;

    public void NotifyQueuedFollowUpsChanged()
    {
        OnPropertyChanged(nameof(QueuedFollowUps));
        OnPropertyChanged(nameof(HasQueuedFollowUps));
        RaiseQueuedFollowUpCommandStates();
    }

    private void MutateQueuedFollowUp(object? parameter, Action<QueuedFollowUp> mutation)
    {
        if (parameter is not QueuedFollowUp item)
        {
            return;
        }

        mutation(item);
        NotifyQueuedFollowUpsChanged();
    }

    private async Task PersistQueuedMutationAsync(
        object? parameter,
        Action<QueuedFollowUp> mutation,
        Func<Task>? persist)
    {
        if (parameter is not QueuedFollowUp item)
        {
            return;
        }

        mutation(item);
        NotifyQueuedFollowUpsChanged();
        if (persist is not null)
        {
            await persist().ConfigureAwait(true);
        }
    }

    private void RaiseQueuedFollowUpCommandStates()
    {
        beginQueuedFollowUpEditCommand.RaiseCanExecuteChanged();
        cancelQueuedFollowUpEditCommand.RaiseCanExecuteChanged();
        saveQueuedFollowUpEditCommand.RaiseCanExecuteChanged();
        moveQueuedFollowUpUpCommand.RaiseCanExecuteChanged();
        moveQueuedFollowUpDownCommand.RaiseCanExecuteChanged();
        deleteQueuedFollowUpCommand.RaiseCanExecuteChanged();
        sendQueuedFollowUpCommand.RaiseCanExecuteChanged();
    }

    private void RaisePromptEditCommandStates()
    {
        beginPromptEditCommand.RaiseCanExecuteChanged();
        cancelPromptEditCommand.RaiseCanExecuteChanged();
        submitPromptEditCommand.RaiseCanExecuteChanged();
    }

    private async Task ToggleDictationAsync()
    {
        try
        {
            if (IsDictating)
            {
                await speechRecognitionService.StopAsync().ConfigureAwait(true);
                IsDictating = false;
                DictationStatusText = "Dictation stopped";
                return;
            }

            DictationStatusText = "Starting dictation...";
            await speechRecognitionService.StartAsync().ConfigureAwait(true);
            IsDictating = true;
            DictationStatusText = "Listening...";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsDictating = false;
            var message = ex.GetBaseException().Message.Trim();
            DictationStatusText = $"Dictation unavailable: {message}";
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs args)
    {
        if (isDisposed)
        {
            return;
        }

        var phrase = args.Text.Trim();
        if (phrase.Length == 0)
        {
            return;
        }

        if (IsTurnRunning)
        {
            SteeringText = AppendRecognizedPhrase(SteeringText, phrase);
        }
        else
        {
            Prompt = AppendRecognizedPhrase(Prompt, phrase);
        }
    }

    private void OnSpeechRecognitionStopped(object? sender, SpeechRecognitionStoppedEventArgs args)
    {
        if (isDisposed)
        {
            return;
        }

        IsDictating = false;
        DictationStatusText = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? "Dictation stopped"
            : $"Dictation unavailable: {args.ErrorMessage.Trim()}";
    }

    private static string AppendRecognizedPhrase(string current, string phrase)
    {
        if (current.Length == 0)
        {
            return phrase;
        }

        return char.IsWhiteSpace(current[^1])
            ? current + phrase
            : current + " " + phrase;
    }

    private void MoveAttachment(AttachmentReference? attachment, int offset)
    {
        if (attachment is null)
        {
            return;
        }

        var currentIndex = Attachments.IndexOf(attachment);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Attachments.Count)
        {
            return;
        }

        Attachments.Move(currentIndex, targetIndex);
        NotifyAttachmentsChanged();
    }

    private void NotifyAttachmentsChanged()
    {
        OnPropertyChanged(nameof(Attachments));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanSubmitAttachments));
        OnPropertyChanged(nameof(AttachmentValidationMessage));
        removeAttachmentCommand.RaiseCanExecuteChanged();
        moveAttachmentLeftCommand.RaiseCanExecuteChanged();
        moveAttachmentRightCommand.RaiseCanExecuteChanged();
        submitCommand.RaiseCanExecuteChanged();
        composerSendCommand.RaiseCanExecuteChanged();
        steerCommand.RaiseCanExecuteChanged();
        alternateFollowUpCommand.RaiseCanExecuteChanged();
        startCodeReviewCommand.RaiseCanExecuteChanged();
        RaisePromptEditCommandStates();
    }

    public void NotifyResponseChanged()
    {
        OnPropertyChanged(nameof(FinalResponse));
        OnPropertyChanged(nameof(ComposerActionLabel));
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(ContextWindowIndicator));
        OnPropertyChanged(nameof(ContextWindowToolTip));
        RefreshFindInChatMatches();
    }

    private void CloseFindInChat()
    {
        IsFindInChatOpen = false;
        if (findInChatText.Length > 0)
        {
            findInChatText = string.Empty;
            OnPropertyChanged(nameof(FindInChatText));
        }
        RefreshFindInChatMatches();
    }

    private void RefreshFindInChatMatches()
    {
        ClearFindMatchFlags();
        findMatches.Clear();
        currentFindMatchIndex = -1;
        var query = FindInChatText.Trim();
        if (query.Length > 0)
        {
            foreach (var turn in ConversationTurns)
            {
                AddFindMatches(turn, turn.UserPrompt, query);
                AddFindMatches(turn, turn.AssistantResponse, query);
                turn.IsFindMatch = findMatches.Contains(turn);
            }
        }

        if (findMatches.Count > 0)
        {
            currentFindMatchIndex = 0;
            findMatches[0].IsCurrentFindMatch = true;
        }

        RaiseFindStateChanged();
    }

    private void AddFindMatches(CodexConversationTurn turn, string text, string query)
    {
        var searchIndex = 0;
        while (searchIndex <= text.Length - query.Length)
        {
            var matchIndex = text.IndexOf(query, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            findMatches.Add(turn);
            searchIndex = matchIndex + query.Length;
        }
    }

    private void MoveFindMatch(int offset)
    {
        if (findMatches.Count == 0)
        {
            return;
        }

        foreach (var turn in ConversationTurns)
        {
            turn.IsCurrentFindMatch = false;
        }

        currentFindMatchIndex = (currentFindMatchIndex + offset + findMatches.Count) % findMatches.Count;
        findMatches[currentFindMatchIndex].IsCurrentFindMatch = true;
        RaiseFindStateChanged();
    }

    private void ClearFindMatchFlags()
    {
        foreach (var turn in ConversationTurns)
        {
            turn.IsFindMatch = false;
            turn.IsCurrentFindMatch = false;
        }
    }

    private void RaiseFindStateChanged()
    {
        OnPropertyChanged(nameof(FindInChatMatchCount));
        OnPropertyChanged(nameof(CurrentFindInChatMatchNumber));
        OnPropertyChanged(nameof(CurrentFindInChatTurn));
        OnPropertyChanged(nameof(FindInChatSummary));
        findNextCommand.RaiseCanExecuteChanged();
        findPreviousCommand.RaiseCanExecuteChanged();
    }

    private static string FormatCompactTokenCount(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}m";
        }

        return value >= 1_000
            ? $"{value / 1_000d:0.#}k"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    public void ApplyModelCatalog(
        IEnumerable<CodexModelOption> models,
        CodexAccountInfo? account)
    {
        ArgumentNullException.ThrowIfNull(models);
        var requestedModel = ModelOverride;
        var requestedReasoning = ReasoningEffortOverride;
        var visibleModels = models
            .Where(model => !model.Hidden)
            .DistinctBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ModelCatalog.Clear();
        ModelOptions.Clear();
        foreach (var model in visibleModels)
        {
            ModelCatalog.Add(model);
            ModelOptions.Add(model.Model);
        }

        AccountPlanLabel = FormatAccountPlan(account);
        ModelCatalogError = string.Empty;
        IsModelCatalogLoading = false;
        IsModelCatalogStale = false;

        reasoningEffortOverride = requestedReasoning;
        var match = visibleModels.FirstOrDefault(model =>
                string.Equals(model.Model, requestedModel, StringComparison.OrdinalIgnoreCase))
            ?? visibleModels.FirstOrDefault(model => model.IsDefault)
            ?? visibleModels.FirstOrDefault();
        if (ReferenceEquals(SelectedModel, match))
        {
            RebuildReasoningOptions();
            ReconcileFastAvailability();
            OnPropertyChanged(nameof(FastModeDescription));
        }
        else
        {
            SelectedModel = match;
        }
        showModelsCommand.RaiseCanExecuteChanged();
        showReasoningCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ModelSelectionSummary));
    }

    public void SetModelCatalogLoading()
    {
        IsModelCatalogLoading = true;
        ModelCatalogError = string.Empty;
    }

    public void SetModelCatalogError(string message)
    {
        IsModelCatalogLoading = false;
        IsModelCatalogStale = true;
        ModelCatalogError = message;
    }

    public void InvalidateModelCatalog()
    {
        IsModelCatalogStale = true;
        AccountPlanLabel = string.Empty;
    }

    public void RaiseCommandStates()
    {
        submitCommand.RaiseCanExecuteChanged();
        composerSendCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
        loadModelsCommand.RaiseCanExecuteChanged();
        steerCommand.RaiseCanExecuteChanged();
        alternateFollowUpCommand.RaiseCanExecuteChanged();
        forkConversationCommand.RaiseCanExecuteChanged();
        removeAttachmentCommand.RaiseCanExecuteChanged();
        moveAttachmentLeftCommand.RaiseCanExecuteChanged();
        moveAttachmentRightCommand.RaiseCanExecuteChanged();
        RaisePromptEditCommandStates();
        RaiseQueuedFollowUpCommandStates();
        openExternalUriCommand.RaiseCanExecuteChanged();
        editGeneratedImageCommand.RaiseCanExecuteChanged();
        openOptionsCommand.RaiseCanExecuteChanged();
        showOptionsMainCommand.RaiseCanExecuteChanged();
        showModelsCommand.RaiseCanExecuteChanged();
        showReasoningCommand.RaiseCanExecuteChanged();
        RaiseAgentCommandStates();
        RaiseGoalCommandStates();
    }

    private bool RoutesToCodeReview =>
        codeReviewActions is not null &&
        string.Equals(Prompt.Trim(), "/review", StringComparison.OrdinalIgnoreCase);

    private void RaiseAgentCommandStates()
    {
        openAgentCommand.RaiseCanExecuteChanged();
        steerAgentCommand.RaiseCanExecuteChanged();
        stopAgentCommand.RaiseCanExecuteChanged();
        closeAgentTranscriptCommand.RaiseCanExecuteChanged();
    }

    private void RebuildReasoningOptions()
    {
        var requested = ParseReasoningEffort(ReasoningEffortOverride);
        ReasoningOptions.Clear();
        ReasoningEffortOptions.Clear();
        foreach (var option in SelectedModel?.SupportedReasoningEfforts ?? [])
        {
            ReasoningOptions.Add(option);
            ReasoningEffortOptions.Add(option.ProtocolValue);
        }

        SelectedReasoning = ReasoningOptions.FirstOrDefault(option => option.Effort == requested)
            ?? ReasoningOptions.FirstOrDefault(option => option.Effort == SelectedModel?.DefaultReasoningEffort)
            ?? ReasoningOptions.FirstOrDefault();
        showReasoningCommand.RaiseCanExecuteChanged();
    }

    private void ReconcileFastAvailability()
    {
        OnPropertyChanged(nameof(IsFastModeAvailable));
        if (!IsFastModeAvailable && ServiceTierSelection == CodexServiceTierSelection.Fast)
        {
            ServiceTierSelection = CodexServiceTierSelection.Standard;
        }
        OnPropertyChanged(nameof(IsFastModeEnabled));
    }

    private static CodexReasoningEffort? ParseReasoningEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => CodexReasoningEffort.None,
        "minimal" => CodexReasoningEffort.Minimal,
        "low" => CodexReasoningEffort.Low,
        "medium" => CodexReasoningEffort.Medium,
        "high" => CodexReasoningEffort.High,
        "xhigh" => CodexReasoningEffort.XHigh,
        _ => null
    };

    private static string FormatAccountPlan(CodexAccountInfo? account)
    {
        if (account is null ||
            !string.Equals(account.Type, "chatgpt", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(account.PlanType))
        {
            return string.Empty;
        }

        var plan = account.PlanType.ToLowerInvariant() switch
        {
            "self_serve_business_usage_based" or "business" => "Business",
            "enterprise_cbp_usage_based" or "enterprise" => "Enterprise",
            "prolite" => "Pro Lite",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(account.PlanType.Replace('_', ' '))
        };
        return $"ChatGPT {plan}";
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        toggleDictationCommand.RaiseCanExecuteChanged();
        speechRecognitionService.SpeechRecognized -= OnSpeechRecognized;
        speechRecognitionService.Stopped -= OnSpeechRecognitionStopped;
        try
        {
            if (IsDictating)
            {
                await speechRecognitionService.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            IsDictating = false;
            await speechRecognitionService.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public enum ComposerOptionsPage
{
    Main,
    Models,
    Reasoning
}
