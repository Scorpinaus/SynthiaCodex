using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskViewModel : ObservableObject
{
    private readonly AsyncRelayCommand submitCommand;
    private readonly AsyncRelayCommand composerSendCommand;
    private readonly AsyncRelayCommand cancelCommand;
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly AsyncRelayCommand steerCommand;
    private readonly AsyncRelayCommand alternateFollowUpCommand;
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
    private readonly List<CodexConversationTurn> findMatches = [];
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

    public TaskViewModel(
        ITurnExecutionActions turnActions,
        IFollowUpManagementActions followUpActions,
        IConversationHistoryActions historyActions,
        IComposerSupportActions composerActions)
    {
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
        SubmitCommand = submitCommand = new AsyncRelayCommand(turnActions.SubmitAsync);
        ComposerSendCommand = composerSendCommand = new AsyncRelayCommand(
            () => IsTurnRunning ? turnActions.SteerAsync() : turnActions.SubmitAsync());
        CancelCommand = cancelCommand = new AsyncRelayCommand(turnActions.CancelAsync, turnActions.CanCancelTurn);
        LoadModelsCommand = loadModelsCommand = new AsyncRelayCommand(composerActions.LoadModelsAsync);
        SteerCommand = steerCommand = new AsyncRelayCommand(turnActions.SteerAsync, turnActions.CanSteerTurn);
        AlternateFollowUpCommand = alternateFollowUpCommand = new AsyncRelayCommand(
            followUpActions.SendAlternateFollowUpAsync,
            turnActions.CanSteerTurn);
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
    }

    public ObservableCollection<CodexTimelineItem> TimelineItems => timelineItems;

    public ObservableCollection<CodexConversationTurn> ConversationTurns => conversationTurns;

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
        OnPropertyChanged(nameof(ContextWindowIndicator));
        OnPropertyChanged(nameof(ContextWindowToolTip));
        OnPropertyChanged(nameof(QueuedFollowUps));
        OnPropertyChanged(nameof(HasQueuedFollowUps));
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
        openOptionsCommand.RaiseCanExecuteChanged();
        showOptionsMainCommand.RaiseCanExecuteChanged();
        showModelsCommand.RaiseCanExecuteChanged();
        showReasoningCommand.RaiseCanExecuteChanged();
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
}

public enum ComposerOptionsPage
{
    Main,
    Models,
    Reasoning
}
