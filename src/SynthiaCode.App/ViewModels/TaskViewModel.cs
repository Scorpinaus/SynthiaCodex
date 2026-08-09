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
    private readonly RelayCommand removeAttachmentCommand;
    private readonly RelayCommand moveAttachmentLeftCommand;
    private readonly RelayCommand moveAttachmentRightCommand;
    private readonly RelayCommand openFindInChatCommand;
    private readonly RelayCommand closeFindInChatCommand;
    private readonly RelayCommand findNextCommand;
    private readonly RelayCommand findPreviousCommand;
    private readonly ISpeechRecognitionService speechRecognitionService;
    private readonly ICodeReviewActions? codeReviewActions;
    private readonly List<CodexConversationTurn> findMatches = [];
    private ConversationWorkspaceSnapshot conversation = ConversationWorkspaceSnapshot.Empty;
    private CodexFollowUpQueue followUpQueue = new();
    private readonly ObservableCollection<CodexTimelineItem> timelineItems = [];
    private readonly ObservableCollection<CodexConversationTurn> conversationTurns = [];
    private readonly ObservableCollection<string> rawEvents = [];
    private string prompt = string.Empty;
    private string submittedPrompt = string.Empty;
    private string steeringText = string.Empty;
    private string appServerHealth = "Codex idle";
    private FollowUpBehavior followUpBehavior = FollowUpBehavior.Queue;
    private bool isTurnRunning;
    private bool isFindInChatOpen;
    private string findInChatText = string.Empty;
    private int currentFindMatchIndex = -1;
    private bool isDictating;
    private bool isDisposed;
    private string dictationStatusText = string.Empty;

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
        OpenFindInChatCommand = openFindInChatCommand = new RelayCommand(
            () => IsFindInChatOpen = true);
        CloseFindInChatCommand = closeFindInChatCommand = new RelayCommand(CloseFindInChat);
        FindNextCommand = findNextCommand = new RelayCommand(
            () => MoveFindMatch(1),
            () => findMatches.Count > 0);
        FindPreviousCommand = findPreviousCommand = new RelayCommand(
            () => MoveFindMatch(-1),
            () => findMatches.Count > 0);
        Agents = new TaskAgentsViewModel(agentActions);
        Agents.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
        Goals = new TaskGoalsViewModel(goalActions, () => ConversationThreadId);
        Goals.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
        Options = new TaskOptionsViewModel(
            composerActions,
            () => IsTurnRunning,
            () =>
            {
                OnPropertyChanged(nameof(CanSubmitAttachments));
                OnPropertyChanged(nameof(AttachmentValidationMessage));
                RaiseCommandStates();
            });
        Options.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
        Conversation = new TaskConversationViewModel(this);
        Composer = new TaskComposerViewModel(this);
    }

    public TaskConversationViewModel Conversation { get; }

    public TaskComposerViewModel Composer { get; }

    public ObservableCollection<CodexTimelineItem> TimelineItems => timelineItems;

    public ObservableCollection<CodexConversationTurn> ConversationTurns => conversationTurns;

    public TaskAgentsViewModel Agents { get; }

    public ObservableCollection<AgentThreadViewModel> ActiveAgents => Agents.ActiveAgents;

    public ObservableCollection<AgentThreadViewModel> DoneAgents => Agents.DoneAgents;

    public bool HasAgents => Agents.HasAgents;

    public bool HasActiveAgents => Agents.HasActiveAgents;

    public bool HasDoneAgents => Agents.HasDoneAgents;

    public AgentThreadViewModel? SelectedAgent => Agents.SelectedAgent;

    public bool IsAgentTranscriptOpen => Agents.IsAgentTranscriptOpen;

    public string? ConversationThreadId => conversation.ThreadId;

    public ObservableCollection<string> RawEvents => rawEvents;

    public ObservableCollection<QueuedFollowUp> QueuedFollowUps => followUpQueue.Items;

    public ObservableCollection<AttachmentReference> Attachments { get; } = [];

    public TaskOptionsViewModel Options { get; }

    public ObservableCollection<string> ModelOptions => Options.ModelOptions;

    public ObservableCollection<CodexModelOption> ModelCatalog => Options.ModelCatalog;

    public ObservableCollection<CodexReasoningOption> ReasoningOptions => Options.ReasoningOptions;

    public ObservableCollection<string> ReasoningEffortOptions => Options.ReasoningEffortOptions;

    public ComposerSkillSelectorViewModel SkillSelector { get; }

    public IReadOnlyList<FollowUpBehavior> FollowUpBehaviorOptions { get; } =
        [FollowUpBehavior.Queue, FollowUpBehavior.Steer];

    public ICommand SubmitCommand { get; }
    public ICommand ComposerSendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LoadModelsCommand => Options.LoadModelsCommand;
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
    public ICommand OpenOptionsCommand => Options.OpenOptionsCommand;
    public ICommand ShowOptionsMainCommand => Options.ShowOptionsMainCommand;
    public ICommand ShowModelsCommand => Options.ShowModelsCommand;
    public ICommand ShowReasoningCommand => Options.ShowReasoningCommand;
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand MoveAttachmentLeftCommand { get; }
    public ICommand MoveAttachmentRightCommand { get; }
    public ICommand OpenFindInChatCommand { get; }
    public ICommand CloseFindInChatCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand FindPreviousCommand { get; }
    public ICommand OpenAgentCommand => Agents.OpenAgentCommand;
    public ICommand SteerAgentCommand => Agents.SteerAgentCommand;
    public ICommand StopAgentCommand => Agents.StopAgentCommand;
    public ICommand CloseAgentTranscriptCommand => Agents.CloseAgentTranscriptCommand;
    public TaskGoalsViewModel Goals { get; }

    public ICommand BeginGoalEditCommand => Goals.BeginGoalEditCommand;
    public ICommand CancelGoalEditCommand => Goals.CancelGoalEditCommand;
    public ICommand SaveGoalCommand => Goals.SaveGoalCommand;
    public ICommand ToggleGoalStatusCommand => Goals.ToggleGoalStatusCommand;
    public ICommand ClearGoalCommand => Goals.ClearGoalCommand;

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

    public CodexThreadGoal? Goal => Goals.Goal;

    public bool IsGoalFeatureAvailable => Goals.IsGoalFeatureAvailable;

    public bool IsGoalSupported => Goals.IsGoalSupported;

    public bool IsGoalLoading => Goals.IsGoalLoading;

    public bool IsGoalEditing => Goals.IsGoalEditing;

    public bool IsGoalBusy => Goals.IsGoalBusy;

    public string GoalDraft
    {
        get => Goals.GoalDraft;
        set => Goals.GoalDraft = value;
    }

    public string GoalError => Goals.GoalError;

    public bool HasGoal => Goals.HasGoal;

    public bool HasGoalError => Goals.HasGoalError;

    public string GoalObjective => Goals.GoalObjective;

    public string GoalStatusLabel => Goals.GoalStatusLabel;

    public string GoalStatusAutomationName => Goals.GoalStatusAutomationName;

    public string GoalToggleActionLabel => Goals.GoalToggleActionLabel;

    public string GoalUsageSummary => Goals.GoalUsageSummary;

    public string GoalCharacterCount => Goals.GoalCharacterCount;

    public string GoalEditorValidationMessage => Goals.GoalEditorValidationMessage;

    public void ResetGoalContext(bool isCodexThread) => Goals.ResetContext(isCodexThread);

    public void SetGoalLoading() => Goals.SetLoading();

    public void ApplyGoal(CodexThreadGoal? value) => Goals.Apply(value);

    public void SetGoalLoadError(string message) => Goals.SetLoadError(message);

    public void SetGoalUnsupported(string message) => Goals.SetUnsupported(message);

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
        get => Options.ModelOverride;
        set => Options.ModelOverride = value;
    }

    public string ReasoningEffortOverride
    {
        get => Options.ReasoningEffortOverride;
        set => Options.ReasoningEffortOverride = value;
    }

    public CodexModelOption? SelectedModel
    {
        get => Options.SelectedModel;
        set => Options.SelectedModel = value;
    }

    public CodexReasoningOption? SelectedReasoning
    {
        get => Options.SelectedReasoning;
        set => Options.SelectedReasoning = value;
    }

    public CodexServiceTierSelection ServiceTierSelection
    {
        get => Options.ServiceTierSelection;
        set => Options.ServiceTierSelection = value;
    }

    public bool IsFastModeEnabled
    {
        get => Options.IsFastModeEnabled;
        set => Options.IsFastModeEnabled = value;
    }

    public bool IsFastModeAvailable => Options.IsFastModeAvailable;

    public string FastModeDescription => Options.FastModeDescription;

    public string ModelSelectionSummary => Options.ModelSelectionSummary;

    public string AccountPlanLabel => Options.AccountPlanLabel;

    public bool HasAccountPlanLabel => Options.HasAccountPlanLabel;

    public bool IsModelCatalogLoading => Options.IsModelCatalogLoading;

    public bool IsModelCatalogStale => Options.IsModelCatalogStale;

    public string ModelCatalogError => Options.ModelCatalogError;

    public bool HasModelCatalogError => Options.HasModelCatalogError;

    public bool IsOptionsFlyoutOpen
    {
        get => Options.IsOptionsFlyoutOpen;
        set => Options.IsOptionsFlyoutOpen = value;
    }

    public ComposerOptionsPage OptionsPage
    {
        get => Options.OptionsPage;
        set => Options.OptionsPage = value;
    }

    public bool IsOptionsMainPage => Options.IsOptionsMainPage;

    public bool IsOptionsModelPage => Options.IsOptionsModelPage;

    public bool IsOptionsReasoningPage => Options.IsOptionsReasoningPage;

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
                    Options.CloseForRunningTurn();
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
        Agents.ApplySnapshot(snapshot);
        RefreshFindInChatMatches();
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
        CodexAccountInfo? account) => Options.ApplyModelCatalog(models, account);

    public void SetModelCatalogLoading() => Options.SetModelCatalogLoading();

    public void SetModelCatalogError(string message) => Options.SetModelCatalogError(message);

    public void InvalidateModelCatalog() => Options.InvalidateModelCatalog();

    public void RaiseCommandStates()
    {
        submitCommand.RaiseCanExecuteChanged();
        composerSendCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
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
        Options.RaiseCommandStates();
        RaiseAgentCommandStates();
        Goals.RaiseCommandStates();
    }

    private bool RoutesToCodeReview =>
        codeReviewActions is not null &&
        string.Equals(Prompt.Trim(), "/review", StringComparison.OrdinalIgnoreCase);

    private void RaiseAgentCommandStates()
        => Agents.RaiseCommandStates();

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
