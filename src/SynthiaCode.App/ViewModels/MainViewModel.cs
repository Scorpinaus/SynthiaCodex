using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Input;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Core.Workspaces;
using SynthiaCode.Infrastructure.Codex;

namespace SynthiaCode.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumInstructionBytes = 64 * 1024;
    private readonly ISettingsStore settingsStore;
    private readonly IAppServerSessionCoordinator appServerSessionCoordinator;
    private readonly IHarnessRuntimeCoordinator harnessRuntimeCoordinator;
    private readonly IFolderPicker folderPicker;
    private readonly IUserInteractionService userInteractionService;
    private readonly IThemeService themeService;
    private readonly ConversationWorkflowController conversationWorkflow;
    private readonly ThreadLifecycleUseCaseService threadLifecycle;
    private readonly ThreadStatePersistenceUseCaseService threadStatePersistence;
    private readonly TurnExecutionUseCaseService turnExecution;
    private readonly CodeReviewUseCaseService codeReview;
    private readonly FollowUpQueueUseCaseService followUpQueue;
    private readonly IGitService gitService;
    private readonly ProjectWorkspaceOperations projectWorkspaceOperations;
    private readonly IAppLogger logger;
    private readonly AttachmentDraftOrchestrationService attachmentDraftService;
    private readonly bool enableGoalMode;
    private readonly CancellationTokenSource appServerWarmUpCancellation = new();
    private readonly SynchronizationContext? synchronizationContext;
    private readonly AsyncRelayCommand submitPromptCommand;
    private readonly AsyncRelayCommand cancelTurnCommand;
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly AsyncRelayCommand exitApplicationCommand;
    private readonly AsyncRelayCommand newThreadCommand;
    private readonly AsyncRelayCommand resumeThreadCommand;
    private readonly AsyncRelayCommand forkThreadCommand;
    private readonly AsyncRelayCommand archiveThreadCommand;
    private readonly AsyncRelayCommand unarchiveThreadCommand;
    private readonly AsyncRelayCommand togglePinThreadCommand;
    private readonly AsyncRelayCommand deleteThreadCommand;
    private readonly AsyncRelayCommand renameThreadCommand;
    private readonly AsyncRelayCommand steerTurnCommand;
    private readonly AsyncRelayCommand removeWorktreeCommand;
    private readonly AsyncRelayCommand saveInstructionSettingsCommand;
    private readonly RelayCommand toggleProjectRailCommand;
    private readonly RelayCommand toggleDetailsPaneCommand;
    private readonly RelayCommand dismissShellOverlayCommand;
    private readonly RelayCommand openChangesCommand;
    private readonly RelayCommand openSettingsCommand;
    private readonly RelayCommand resetInstructionSettingsCommand;

    private AppSettings settings = new();
    private CodexInstallation currentCodex => DiagnosticsViewModel.Installation;
    private AuthenticationState currentAuth => DiagnosticsViewModel.Authentication;
    private string selectedTheme = "System";
    private string statusMessage = "Starting";
    private string developerInstructions = string.Empty;
    private string baseInstructions = string.Empty;
    private string instructionSettingsValidationMessage = string.Empty;
    private bool developerInstructionsEnabled;
    private bool baseInstructionsEnabled;
    private bool instructionSettingsInitialized;
    private bool isProjectRailOpen = true;
    private bool isDetailsPaneOpen;
    private bool isCompactLayout;
    private double viewportWidth = 1240;
    private int selectedWorkspaceTabIndex;
    private int selectedInspectorTabIndex;
    private bool executionPolicyLoaded;
    private string? executionPolicyCwd;
    private bool isShuttingDown;
    private bool isRestoringAttachmentDraft;
    private bool isRestoringReviewCommentDraft;
    private string? generalWorkspacePath;
    private string? generalWorkspaceError;
    private Task? shutdownTask;
    private Task? appServerWarmUpTask;

    public MainViewModel(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        IAppServerSessionCoordinator appServerSessionCoordinator,
        IHarnessRuntimeCoordinator harnessRuntimeCoordinator,
        IAuthService authService,
        IFolderPicker folderPicker,
        IUserInteractionService userInteractionService,
        IThemeService themeService,
        ICodexCliUtilityRunner codexCliUtilityRunner,
        ITerminalService terminalService,
        IAppLogger logger,
        ConversationWorkflowController conversationWorkflow,
        ThreadLifecycleUseCaseService threadLifecycle,
        ThreadStatePersistenceUseCaseService threadStatePersistence,
        TurnExecutionUseCaseService turnExecution,
        CodeReviewUseCaseService codeReview,
        FollowUpQueueUseCaseService followUpQueue,
        IGitService gitService,
        ProjectWorkspaceOperations projectWorkspaceOperations,
        AttachmentDraftOrchestrationService attachmentDraftService,
        ISharedCodexConfigurationService sharedCodexConfigurationService,
        ISpeechRecognitionService? speechRecognitionService = null,
        bool enableGoalMode = true)
    {
        this.settingsStore = settingsStore;
        this.appServerSessionCoordinator = appServerSessionCoordinator;
        this.harnessRuntimeCoordinator = harnessRuntimeCoordinator;
        this.folderPicker = folderPicker;
        this.userInteractionService = userInteractionService;
        this.themeService = themeService;
        this.logger = logger;
        this.conversationWorkflow = conversationWorkflow;
        this.threadLifecycle = threadLifecycle;
        this.threadStatePersistence = threadStatePersistence;
        this.turnExecution = turnExecution;
        this.codeReview = codeReview;
        this.followUpQueue = followUpQueue;
        this.gitService = gitService;
        this.projectWorkspaceOperations = projectWorkspaceOperations;
        this.attachmentDraftService = attachmentDraftService;
        this.enableGoalMode = enableGoalMode;
        CodexConfiguration = new CodexConfigurationViewModel(
            sharedCodexConfigurationService,
            GetActiveWorkspacePathIfAvailable,
            userInteractionService.OpenInEditor,
            userInteractionService.RevealInExplorer,
            () => IsShuttingDown,
            message => StatusMessage = message,
            logger);
        synchronizationContext = SynchronizationContext.Current;
        Skills = new SkillsViewModel(
            appServerSessionCoordinator,
            GetActiveWorkspacePathIfAvailable,
            () => ActiveWorkspaceLabel,
            userInteractionService.OpenInEditor,
            userInteractionService.RevealInExplorer,
            () => IsShuttingDown,
            message => StatusMessage = message,
            logger);
        EffectiveCodexSettings = new EffectiveCodexSettingsViewModel(
            appServerSessionCoordinator,
            GetActiveWorkspacePathIfAvailable,
            () => IsShuttingDown,
            logger);
        appServerSessionCoordinator.NotificationReceived += OnAppServerNotificationReceived;
        appServerSessionCoordinator.ServerRequestReceived += OnServerRequestReceived;
        appServerSessionCoordinator.ConnectionFailed += OnAppServerConnectionFailed;
        appServerSessionCoordinator.StateChanged += OnAppServerStateChanged;
        harnessRuntimeCoordinator.EventReceived += OnHarnessEventReceived;

        DiagnosticsViewModel = new DiagnosticsViewModel(
            codexDiscoveryService,
            authService,
            codexCliUtilityRunner,
            logger,
            () => settings.PreferredCodexPath,
            () => IsShuttingDown,
            message => StatusMessage = message,
            settingsStore.SettingsPath);
        DiagnosticsViewModel.EnvironmentChanged += (_, _) => RaiseComputedProperties();
        DiagnosticsViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiagnosticsViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(IsBusy));
            }
        };

        Git = projectWorkspaceOperations.CreateGitViewModel(
            userInteractionService,
            logger,
            CreateGitContext,
            () => IsShuttingDown,
            message => StatusMessage = message);
        Git.PropertyChanged += (_, args) => RelayGitPropertyChanged(args.PropertyName);

        TaskWorkspace = new TaskViewModel(
            new TurnExecutionActionAdapter(this),
            new FollowUpManagementActionAdapter(this),
            new ConversationHistoryActionAdapter(this),
            new ComposerSupportActionAdapter(this),
            new AgentManagementActionAdapter(this),
            speechRecognitionService,
            new GoalManagementActionAdapter(this),
            new CodeReviewActionAdapter(this));
        TaskWorkspace.PropertyChanged += (_, args) => RelayTaskPropertyChanged(args.PropertyName);

        ApprovalQueue = new ApprovalQueueViewModel(appServerSessionCoordinator.RespondToServerRequestAsync);
        ExecutionPolicy = new ExecutionPolicyViewModel(
            userInteractionService.ConfirmDestructiveAction,
            OnExecutionPolicyChanged);

        ProjectWorkspace = new ProjectThreadViewModel(
            new ProjectNavigationActionAdapter(this),
            new ThreadLifecycleActionAdapter(this));
        ProjectWorkspace.PropertyChanged += (_, args) => RelayProjectPropertyChanged(args.PropertyName);

        BrowseProjectCommand = ProjectWorkspace.BrowseProjectCommand;
        RefreshDiagnosticsCommand = DiagnosticsViewModel.RefreshCommand;
        RunCodexDoctorCommand = DiagnosticsViewModel.RunDoctorCommand;
        NewThreadCommand = newThreadCommand = (AsyncRelayCommand)ProjectWorkspace.NewThreadCommand;
        ResumeThreadCommand = resumeThreadCommand = (AsyncRelayCommand)ProjectWorkspace.ResumeThreadCommand;
        ForkThreadCommand = forkThreadCommand = (AsyncRelayCommand)ProjectWorkspace.ForkThreadCommand;
        ArchiveThreadCommand = archiveThreadCommand = (AsyncRelayCommand)ProjectWorkspace.ArchiveThreadCommand;
        UnarchiveThreadCommand = unarchiveThreadCommand = (AsyncRelayCommand)ProjectWorkspace.UnarchiveThreadCommand;
        TogglePinThreadCommand = togglePinThreadCommand = (AsyncRelayCommand)ProjectWorkspace.TogglePinThreadCommand;
        DeleteThreadCommand = deleteThreadCommand = (AsyncRelayCommand)ProjectWorkspace.DeleteThreadCommand;
        RenameThreadCommand = renameThreadCommand = (AsyncRelayCommand)ProjectWorkspace.RenameThreadCommand;
        SteerTurnCommand = steerTurnCommand = (AsyncRelayCommand)TaskWorkspace.SteerCommand;
        RemoveWorktreeCommand = removeWorktreeCommand = (AsyncRelayCommand)ProjectWorkspace.RemoveWorktreeCommand;
        OpenRecentProjectCommand = ProjectWorkspace.OpenRecentProjectCommand;
        SignInChatGptCommand = DiagnosticsViewModel.SignInChatGptCommand;
        SignInDeviceCodeCommand = DiagnosticsViewModel.SignInDeviceCodeCommand;
        SignOutCommand = DiagnosticsViewModel.SignOutCommand;
        SubmitPromptCommand = submitPromptCommand = (AsyncRelayCommand)TaskWorkspace.SubmitCommand;
        CancelTurnCommand = cancelTurnCommand = (AsyncRelayCommand)TaskWorkspace.CancelCommand;
        LoadModelsCommand = loadModelsCommand = (AsyncRelayCommand)TaskWorkspace.LoadModelsCommand;
        ExitApplicationCommand = exitApplicationCommand = new AsyncRelayCommand(RequestApplicationExitAsync, () => !isShuttingDown);
        SaveInstructionSettingsCommand = saveInstructionSettingsCommand =
            new AsyncRelayCommand(SaveInstructionSettingsAsync, CanSaveInstructionSettings);
        ResetInstructionSettingsCommand = resetInstructionSettingsCommand =
            new RelayCommand(ResetInstructionSettings, () => !IsShuttingDown);
        RefreshGitCommand = Git.RefreshCommand;
        ShowWorkingDiffCommand = Git.ShowWorkingDiffCommand;
        ShowStagedDiffCommand = Git.ShowStagedDiffCommand;
        StageSelectedFileCommand = Git.StageCommand;
        UnstageSelectedFileCommand = Git.UnstageCommand;
        RevertSelectedFileCommand = Git.DiscardCommand;
        CommitCommand = Git.CommitCommand;
        OpenInEditorCommand = Git.OpenEditorCommand;
        RevealInExplorerCommand = Git.RevealExplorerCommand;
        Terminal = new TerminalViewModel(
            terminalService,
            logger,
            CreateTerminalContext,
            () => IsShuttingDown,
            message => StatusMessage = message,
            () => SelectedWorkspaceTabIndex = 1);
        Terminal.PropertyChanged += (_, args) => RelayTerminalPropertyChanged(args.PropertyName);
        StartTerminalCommand = Terminal.StartCommand;
        SendTerminalInputCommand = Terminal.SendInputCommand;
        KillTerminalCommand = Terminal.KillCommand;
        ClearTerminalCommand = Terminal.ClearCommand;
        ToggleTerminalCommand = Terminal.ToggleCommand;
        ToggleProjectRailCommand = toggleProjectRailCommand = new RelayCommand(ToggleProjectRail, () => !IsShuttingDown);
        ToggleDetailsPaneCommand = toggleDetailsPaneCommand = new RelayCommand(ToggleDetailsPane, () => !IsShuttingDown);
        DismissShellOverlayCommand = dismissShellOverlayCommand = new RelayCommand(DismissShellOverlay, () => !IsShuttingDown);
        OpenChangesCommand = openChangesCommand = new RelayCommand(OpenChanges, () => !IsShuttingDown);
        OpenSettingsCommand = openSettingsCommand = new RelayCommand(OpenSettings, () => !IsShuttingDown);
        Account = new AccountViewModel(
            cancellationToken => appServerSessionCoordinator.ReadAccountAsync(false, cancellationToken),
            appServerSessionCoordinator.ReadAccountRateLimitsAsync,
            OpenSettings,
            SignInChatGptCommand,
            SignOutCommand,
            logger);
    }

    private string? activeThreadId
    {
        get => conversationWorkflow.ActiveThreadId;
        set => conversationWorkflow.Select(value);
    }

    private string? activeTurnId
    {
        get => conversationWorkflow.ActiveTurnId;
        set => conversationWorkflow.SetActiveTurn(value);
    }

    private bool activeThreadLoaded
    {
        get => conversationWorkflow.ActiveThreadLoaded;
        set => conversationWorkflow.SetActiveThreadLoaded(value);
    }


    public event EventHandler? CloseRequested;

    public async Task AddImageFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var result = await attachmentDraftService.ImportImagesAsync(paths, cancellationToken).ConfigureAwait(true);
        foreach (var attachment in result.Attachments)
        {
            TaskWorkspace.AddAttachment(attachment);
        }
        StatusMessage = result.ToStatusMessage("image");
    }

    private async Task BeginGeneratedImageEditAsync(string path)
    {
        try
        {
            var editSelection = userInteractionService.SelectGeneratedImageEdit(path);
            if (editSelection is null)
            {
                StatusMessage = "Image edit canceled.";
                return;
            }

            var result = await attachmentDraftService.ImportImagesAsync([path]).ConfigureAwait(true);
            if (result.Attachments.Count == 0)
            {
                StatusMessage = result.Failures.FirstOrDefault() is { } failure
                    ? $"Could not prepare image for editing: {failure}"
                    : $"Could not prepare {Path.GetFileName(path)} for editing.";
                return;
            }

            var attachment = result.Attachments[0];
            AttachmentReference? regionGuide = null;
            if (editSelection.HasRegionGuide)
            {
                await using var guideStream = new MemoryStream(
                    editSelection.RegionGuidePng!,
                    writable: false);
                var sourceName = Path.GetFileNameWithoutExtension(path);
                regionGuide = await attachmentDraftService.ImportPastedImageAsync(
                    guideStream,
                    $"{sourceName}-edit-region.png").ConfigureAwait(true);
            }

            TaskWorkspace.AddAttachment(attachment);
            if (regionGuide is not null)
            {
                TaskWorkspace.AddAttachment(regionGuide);
            }

            var editPrompt = regionGuide is null
                ? $"$imagegen Edit the attached image \"{attachment.DisplayName}\": "
                : $"$imagegen Edit the attached source image \"{attachment.DisplayName}\". " +
                  $"The companion image \"{regionGuide.DisplayName}\" is an edit-region guide; " +
                  "the translucent red mark identifies the area to change. Preserve everything " +
                  "outside the marked area. Requested change: ";
            TaskWorkspace.Prompt = string.IsNullOrWhiteSpace(TaskWorkspace.Prompt)
                ? editPrompt
                : editPrompt + TaskWorkspace.Prompt.Trim();
            StatusMessage = regionGuide is null
                ? "Generated image attached. Describe the edit, then send."
                : "Generated image and marked region attached. Describe the edit, then send.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusMessage = $"Could not prepare {Path.GetFileName(path)} for editing: {ex.Message}";
        }
    }

    public async Task AddAttachmentPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string workspacePath;
        try
        {
            workspacePath = GetActiveWorkspacePath();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return;
        }
        var result = await attachmentDraftService
            .ImportPathsAsync(paths, GetActiveWorkspaceRoots(), cancellationToken)
            .ConfigureAwait(true);
        foreach (var attachment in result.Attachments)
        {
            TaskWorkspace.AddAttachment(attachment);
        }
        StatusMessage = result.ToStatusMessage("attachment");
    }

    public Task AddWorkspaceFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        AddAttachmentPathsAsync(paths, cancellationToken);

    public Task AddWorkspaceFolderAsync(string path, CancellationToken cancellationToken = default) =>
        AddAttachmentPathsAsync([path], cancellationToken);

    public async Task AddPastedImageAsync(
        Stream imageStream,
        string displayName = "pasted-image.png",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        var attachment = await attachmentDraftService
            .ImportPastedImageAsync(imageStream, displayName, cancellationToken)
            .ConfigureAwait(true);
        TaskWorkspace.AddAttachment(attachment);
        StatusMessage = "Added pasted image";
    }

    public void ReportAttachmentError(string message) =>
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Could not add the attachment." : message;

    public void OpenAttachment(AttachmentReference attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var path = attachmentDraftService.ResolveOpenPath(
            GetActiveWorkspacePath(),
            attachment,
            GetActiveWorkspaceRoots());
        var exists = attachment.IsFolder ? Directory.Exists(path) : File.Exists(path);
        if (string.IsNullOrWhiteSpace(path) || !exists)
        {
            throw new FileNotFoundException($"Attachment '{attachment.DisplayName}' is unavailable.", path);
        }
        if (attachment.IsFolder)
        {
            userInteractionService.RevealInExplorer(path);
        }
        else
        {
            userInteractionService.OpenInEditor(path);
        }
    }

    private void CaptureAttachmentDraft(string? projectPath, string? threadId)
    {
        if (!isRestoringAttachmentDraft) attachmentDraftService.CaptureDraft(settings, projectPath, threadId, TaskWorkspace.Attachments);
    }

    private void RestoreAttachmentDraft(string? projectPath, string? threadId)
    {
        isRestoringAttachmentDraft = true;
        try
        {
            TaskWorkspace.ReplaceAttachments(attachmentDraftService.RestoreDraft(
                settings,
                projectPath,
                threadId,
                GetActiveWorkspacePath(),
                GetActiveWorkspaceRoots()));
        }
        finally
        {
            isRestoringAttachmentDraft = false;
        }
    }

    private async Task SaveAttachmentDraftAsync()
    {
        if (isRestoringAttachmentDraft)
        {
            return;
        }
        CaptureAttachmentDraft(SelectedProjectPath, activeThreadId);
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "attachment_draft_save_failed", "Could not save the image draft.", exception: ex);
        }
    }

    public ObservableCollection<RecentProject> RecentProjects => ProjectWorkspace.RecentProjects;

    public ObservableCollection<string> Diagnostics => DiagnosticsViewModel.Lines;

    public ObservableCollection<CodexTimelineItem> TimelineItems => TaskWorkspace.TimelineItems;

    public ObservableCollection<string> RawEvents => TaskWorkspace.RawEvents;

    public ObservableCollection<string> ModelOptions => TaskWorkspace.ModelOptions;

    public ObservableCollection<GitChangedFile> ChangedFiles => Git.ChangedFiles;

    public ObservableCollection<ProjectThreadState> ProjectThreads => ProjectWorkspace.Threads;

    public TerminalViewModel Terminal { get; }

    public DiagnosticsViewModel DiagnosticsViewModel { get; }

    public CodexConfigurationViewModel CodexConfiguration { get; }

    public SkillsViewModel Skills { get; }

    public EffectiveCodexSettingsViewModel EffectiveCodexSettings { get; }

    public AccountViewModel Account { get; }

    public ApprovalQueueViewModel ApprovalQueue { get; }

    public ExecutionPolicyViewModel ExecutionPolicy { get; }

    public GitViewModel Git { get; }

    public ProjectThreadViewModel ProjectWorkspace { get; }

    public TaskViewModel TaskWorkspace { get; }

    public ObservableCollection<string> ReasoningEffortOptions => TaskWorkspace.ReasoningEffortOptions;

    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public IReadOnlyList<string> WorkspaceModeOptions => ProjectWorkspace.WorkspaceModeOptions;

    public ICommand BrowseProjectCommand { get; }

    public ICommand RefreshDiagnosticsCommand { get; }

    public ICommand RunCodexDoctorCommand { get; }

    public ICommand NewThreadCommand { get; }

    public ICommand ResumeThreadCommand { get; }

    public ICommand ForkThreadCommand { get; }

    public ICommand ArchiveThreadCommand { get; }

    public ICommand UnarchiveThreadCommand { get; }

    public ICommand TogglePinThreadCommand { get; }

    public ICommand DeleteThreadCommand { get; }

    public ICommand RenameThreadCommand { get; }

    public ICommand SteerTurnCommand { get; }

    public ICommand RemoveWorktreeCommand { get; }

    public ICommand OpenRecentProjectCommand { get; }

    public ICommand SignInChatGptCommand { get; }

    public ICommand SignInDeviceCodeCommand { get; }

    public ICommand SignOutCommand { get; }

    public ICommand SubmitPromptCommand { get; }

    public ICommand CancelTurnCommand { get; }

    public ICommand LoadModelsCommand { get; }

    public ICommand ExitApplicationCommand { get; }

    public ICommand RefreshGitCommand { get; }

    public ICommand ShowWorkingDiffCommand { get; }

    public ICommand ShowStagedDiffCommand { get; }

    public ICommand StageSelectedFileCommand { get; }

    public ICommand UnstageSelectedFileCommand { get; }

    public ICommand RevertSelectedFileCommand { get; }

    public ICommand CommitCommand { get; }

    public ICommand OpenInEditorCommand { get; }

    public ICommand RevealInExplorerCommand { get; }

    public ICommand StartTerminalCommand { get; }

    public ICommand SendTerminalInputCommand { get; }

    public ICommand KillTerminalCommand { get; }

    public ICommand ClearTerminalCommand { get; }

    public ICommand ToggleTerminalCommand { get; }

    public ICommand ToggleProjectRailCommand { get; }

    public ICommand ToggleDetailsPaneCommand { get; }

    public ICommand DismissShellOverlayCommand { get; }

    public ICommand OpenChangesCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand SaveInstructionSettingsCommand { get; }

    public ICommand ResetInstructionSettingsCommand { get; }

    public string? SelectedProjectPath
    {
        get => ProjectWorkspace.SelectedProjectPath;
        private set
        {
            ProjectWorkspace.SetSelectedProjectPath(value);
            Git.RaiseCommandStates();
            Terminal.RefreshContext();
        }
    }

    public string SelectedProjectName => ProjectWorkspace.SelectedProjectName;

    public string NewThreadWorkspaceMode
    {
        get => ProjectWorkspace.NewThreadWorkspaceMode;
        set => ProjectWorkspace.NewThreadWorkspaceMode = value;
    }

    public string ActiveWorkspacePath => ProjectWorkspace.ActiveWorkspacePath;

    public string ActiveWorkspaceLabel => ProjectWorkspace.ActiveWorkspaceLabel;

    public bool IsTerminalVisible
    {
        get => Terminal.IsVisible;
        set => Terminal.IsVisible = value;
    }

    public bool IsProjectRailOpen
    {
        get => isProjectRailOpen;
        private set
        {
            if (SetProperty(ref isProjectRailOpen, value))
            {
                RaiseShellVisibilityProperties();
            }
        }
    }

    public bool IsDetailsPaneOpen
    {
        get => isDetailsPaneOpen;
        private set
        {
            if (SetProperty(ref isDetailsPaneOpen, value))
            {
                RaiseShellVisibilityProperties();
                UpdateSettingsSurfaceActivity();
            }
        }
    }

    public bool IsCompactLayout
    {
        get => isCompactLayout;
        private set => SetProperty(ref isCompactLayout, value);
    }

    public bool IsMediumLayout => !IsCompactLayout && !IsWideLayout;

    public bool IsWideLayout => viewportWidth >= 1440;

    public bool IsProjectRailPersistentVisible => IsProjectRailOpen && !IsCompactLayout;

    public bool IsProjectRailOverlayVisible => IsProjectRailOpen && IsCompactLayout;

    public bool IsInspectorPersistentVisible => IsDetailsPaneOpen && IsWideLayout;

    public bool IsInspectorOverlayVisible => IsDetailsPaneOpen && !IsWideLayout;

    public bool IsShellOverlayVisible => IsProjectRailOverlayVisible || IsInspectorOverlayVisible;

    public int SelectedWorkspaceTabIndex
    {
        get => selectedWorkspaceTabIndex;
        set => SetProperty(ref selectedWorkspaceTabIndex, Math.Clamp(value, 0, 2));
    }

    public int SelectedInspectorTabIndex
    {
        get => selectedInspectorTabIndex;
        set
        {
            if (SetProperty(ref selectedInspectorTabIndex, Math.Clamp(value, 0, 1)))
            {
                UpdateSettingsSurfaceActivity();
            }
        }
    }

    public string TerminalInput
    {
        get => Terminal.Input;
        set => Terminal.Input = value;
    }

    public string TerminalOutput
    {
        get => Terminal.Output;
    }

    public string TerminalStatus
    {
        get => Terminal.Status;
    }

    public string TerminalWorkingDirectory
    {
        get => Terminal.WorkingDirectory;
    }

    public bool IsTerminalRunning => Terminal.IsRunning;

    public string CodexSummary => currentCodex.Summary;

    public string CodexExecutablePath => currentCodex.ExecutablePath ?? "Not found";

    public string CodexVersion => currentCodex.Version ?? "Unknown";

    public string AuthSummary => currentAuth.Summary;

    public string AuthDetail => currentAuth.Detail;

    public string CodexHome => currentAuth.CodexHome ?? "Default not resolved";

    public string SettingsPath => settingsStore.SettingsPath;

    public string PromptText
    {
        get => TaskWorkspace.Prompt;
        set => TaskWorkspace.Prompt = value;
    }

    public string ModelOverride
    {
        get => TaskWorkspace.ModelOverride;
        set => TaskWorkspace.ModelOverride = value;
    }

    public string ReasoningEffortOverride
    {
        get => TaskWorkspace.ReasoningEffortOverride;
        set => TaskWorkspace.ReasoningEffortOverride = value;
    }

    public string SelectedTheme
    {
        get => selectedTheme;
        set
        {
            var normalized = NormalizeTheme(value);
            if (!SetProperty(ref selectedTheme, normalized))
            {
                return;
            }

            themeService.ApplyTheme(normalized);
            settings.Theme = normalized;
            _ = SaveThemeSelectionAsync();
        }
    }

    public bool DeveloperInstructionsEnabled
    {
        get => developerInstructionsEnabled;
        set
        {
            if (SetProperty(ref developerInstructionsEnabled, value))
            {
                RefreshInstructionSettingsState();
            }
        }
    }

    public string DeveloperInstructions
    {
        get => developerInstructions;
        set
        {
            if (SetProperty(ref developerInstructions, value ?? string.Empty))
            {
                RefreshInstructionSettingsState();
            }
        }
    }

    public bool BaseInstructionsEnabled
    {
        get => baseInstructionsEnabled;
        set
        {
            if (SetProperty(ref baseInstructionsEnabled, value))
            {
                RefreshInstructionSettingsState();
            }
        }
    }

    public string BaseInstructions
    {
        get => baseInstructions;
        set
        {
            if (SetProperty(ref baseInstructions, value ?? string.Empty))
            {
                RefreshInstructionSettingsState();
            }
        }
    }

    public string InstructionSettingsValidationMessage
    {
        get => instructionSettingsValidationMessage;
        private set => SetProperty(ref instructionSettingsValidationMessage, value);
    }

    public bool HasInstructionSettingsChanges =>
        DeveloperInstructionsEnabled != settings.CustomDeveloperInstructionsEnabled ||
        !string.Equals(DeveloperInstructions, settings.CustomDeveloperInstructions, StringComparison.Ordinal) ||
        BaseInstructionsEnabled != settings.CustomBaseInstructionsEnabled ||
        !string.Equals(BaseInstructions, settings.CustomBaseInstructions, StringComparison.Ordinal);

    public ProjectThreadState? SelectedThread
    {
        get => ProjectWorkspace.SelectedThread;
        set => ProjectWorkspace.SelectedThread = value;
    }

    public string SteeringText
    {
        get => TaskWorkspace.SteeringText;
        set => TaskWorkspace.SteeringText = value;
    }

    public string AppServerHealth
    {
        get => TaskWorkspace.AppServerHealth;
        private set => TaskWorkspace.AppServerHealth = value;
    }

    public string FinalResponse => TaskWorkspace.FinalResponse;

    public bool SupportsSkills => Supports(HarnessCapability.Skills, SelectedThread);

    public bool SupportsCodexSettings =>
        ResolveHarnessId(SelectedThread) == HarnessId.Codex &&
        (Supports(HarnessCapability.Skills, SelectedThread) ||
         Supports(HarnessCapability.Configuration, SelectedThread));

    public bool SupportsCodeReview =>
        IsGitRepository &&
        !string.IsNullOrWhiteSpace(SelectedProjectPath) &&
        (SelectedThread is null || ResolveHarnessId(SelectedThread) == HarnessId.Codex);

    public string GitBranch => Git.Branch;

    public string GitStatusMessage
    {
        get => Git.StatusMessage;
    }

    public bool IsGitRepository => Git.IsRepository;

    public GitChangedFile? SelectedGitFile
    {
        get => Git.SelectedFile;
        set => Git.SelectedFile = value;
    }

    public string SelectedDiff
    {
        get => Git.SelectedDiff;
    }

    public string DiffViewLabel => Git.DiffViewLabel;

    public string CommitMessage
    {
        get => Git.CommitMessage;
        set => Git.CommitMessage = value;
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsBusy => DiagnosticsViewModel.IsBusy;

    public bool IsGitBusy
    {
        get => Git.IsBusy;
    }

    public bool IsTurnRunning
    {
        get => TaskWorkspace.IsTurnRunning;
        private set
        {
            TaskWorkspace.IsTurnRunning = value;
            OnPropertyChanged(nameof(CanChangeExecutionPolicy));
            ProjectWorkspace.RaiseCommandStates();
        }
    }

    public bool CanChangeExecutionPolicy => !IsTurnRunning;

    public bool IsShuttingDown
    {
        get => isShuttingDown;
        private set
        {
            if (SetProperty(ref isShuttingDown, value))
            {
                exitApplicationCommand.RaiseCanExecuteChanged();
                submitPromptCommand.RaiseCanExecuteChanged();
                cancelTurnCommand.RaiseCanExecuteChanged();
                loadModelsCommand.RaiseCanExecuteChanged();
                ProjectWorkspace.RaiseCommandStates();
                TaskWorkspace.RaiseCommandStates();
                Git.RaiseCommandStates();
                Terminal.RaiseCommandStates();
                toggleProjectRailCommand.RaiseCanExecuteChanged();
                toggleDetailsPaneCommand.RaiseCanExecuteChanged();
                dismissShellOverlayCommand.RaiseCanExecuteChanged();
                openChangesCommand.RaiseCanExecuteChanged();
                openSettingsCommand.RaiseCanExecuteChanged();
                CodexConfiguration.RaiseCommandStates();
                Skills.RaiseCommandStates();
                EffectiveCodexSettings.RaiseCommandStates();
            }
        }
    }

    public async Task InitializeAsync()
    {
        logger.Log(AppLogLevel.Information, "view_model_initialize", "Main view model initialization started.");
        settings = await settingsStore.LoadAsync().ConfigureAwait(true);
        try
        {
            generalWorkspacePath = projectWorkspaceOperations.EnsureGeneralWorkspace();
            generalWorkspaceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            generalWorkspacePath = null;
            generalWorkspaceError = $"The General workspace is unavailable: {ex.Message}";
            logger.Log(AppLogLevel.Error, "general_workspace_unavailable", generalWorkspaceError, exception: ex);
        }
        ProjectWorkspace.SetGeneralWorkspacePath(generalWorkspacePath);
        await RestoreAndCleanupAttachmentsAsync().ConfigureAwait(true);
        IsProjectRailOpen = settings.IsProjectRailOpen;
        IsDetailsPaneOpen = settings.IsDetailsPaneOpen;
        selectedTheme = NormalizeTheme(settings.Theme);
        OnPropertyChanged(nameof(SelectedTheme));
        themeService.ApplyTheme(selectedTheme);
        ModelOverride = settings.LastModelOverride ?? string.Empty;
        ReasoningEffortOverride = settings.LastReasoningEffortOverride ?? string.Empty;
        TaskWorkspace.ServiceTierSelection = ParseServiceTierSelection(settings.LastServiceTierOverride);
        TaskWorkspace.FollowUpBehavior = settings.FollowUpBehavior.ParseFollowUpBehavior();
        LoadInstructionSettings();
        var permissionSettingsMigrated = AppSettingsPermissionMigration.Migrate(settings);
        ExecutionPolicy.Initialize(
            settings.PermissionMode,
            settings.CustomPermissionProfileId,
            settings.SandboxModeOverride,
            settings.ApprovalPolicyOverride);
        if (permissionSettingsMigrated)
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        RefreshRecentProjects();
        RestorePersistedThreadState();
        await DiagnosticsViewModel.RefreshAsync().ConfigureAwait(true);
        StatusMessage = "Ready";
        appServerWarmUpTask = WarmUpAppServerAsync(appServerWarmUpCancellation.Token);
    }

    private async Task RestoreAndCleanupAttachmentsAsync()
    {
        await attachmentDraftService.RestoreAndCleanupPersistedAttachmentsAsync(settings).ConfigureAwait(true);
    }

    private async Task WarmUpAppServerAsync(CancellationToken cancellationToken)
    {
        if (!currentCodex.IsFound)
        {
            AppServerHealth = "Codex unavailable";
            return;
        }

        if (currentAuth.Readiness is AuthReadiness.Unavailable or AuthReadiness.NotSignedIn)
        {
            AppServerHealth = "Sign-in needed";
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppServerHealth = "Codex unavailable";
            StatusMessage = "Ready";
            logger.Log(
                AppLogLevel.Warning,
                "app_server_warm_up_failed",
                "Codex app-server warm-up failed; the next Codex action will retry.",
                exception: ex);
        }
    }

    private async Task BrowseProjectAsync()
    {
        var selectedPath = folderPicker.PickFolder(SelectedProjectPath);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        await SelectProjectAsync(selectedPath).ConfigureAwait(true);
    }

    private async Task OpenRecentProjectAsync(object? parameter)
    {
        if (parameter is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            StatusMessage = "Recent project path no longer exists";
            return;
        }

        await SelectProjectAsync(path).ConfigureAwait(true);
    }

    private bool CanEditProject(object? parameter)
    {
        if (IsShuttingDown || parameter is not string path || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var project = settings.RecentProjects.FirstOrDefault(item =>
            ProjectFolderSet.PathsEqual(item.Path, path));
        return project is not null && !settings.ProjectThreads.Any(thread =>
            thread.ScopeKind == ThreadScopeKind.Project &&
            ProjectFolderSet.PathsEqual(thread.ProjectPath, project.Path) &&
            (thread.IsRunning || conversationWorkflow.IsRunning(thread.ThreadId)));
    }

    private async Task EditProjectAsync(object? parameter)
    {
        if (!CanEditProject(parameter) || parameter is not string path)
        {
            StatusMessage = "Wait for project tasks to finish before editing its folders";
            return;
        }

        var project = settings.RecentProjects.First(item =>
            ProjectFolderSet.PathsEqual(item.Path, path));
        var selection = userInteractionService.EditProjectFolders(project);
        if (selection is null)
        {
            return;
        }

        var snapshot = SettingsStorageMapper.Clone(settings);
        var wasSelected = ProjectFolderSet.PathsEqual(SelectedProjectPath, project.Path);
        try
        {
            var result = projectWorkspaceOperations.UpdateProjectFolders(
                settings,
                new ProjectFolderUpdateRequest(
                    project.Path,
                    selection.PrimaryPath,
                    selection.FolderPaths));
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);

            if (wasSelected)
            {
                SelectedProjectPath = result.Project.Path;
                RestorePersistedThreadState();
                Terminal.RefreshContext();
                NotifyCodexContextChanged();
            }
            else
            {
                RefreshRecentProjects();
            }

            await Git.RefreshAsync().ConfigureAwait(true);
            StatusMessage = result.Project.FolderPaths.Count == 1
                ? $"Updated {result.Project.Name}"
                : $"Updated {result.Project.Name} with {result.Project.FolderPaths.Count} folders";
        }
        catch (Exception ex)
        {
            settings.RecentProjects = snapshot.RecentProjects;
            settings.ProjectThreads = snapshot.ProjectThreads;
            settings.ComposerAttachmentDrafts = snapshot.ComposerAttachmentDrafts;
            if (wasSelected)
            {
                SelectedProjectPath = project.Path;
                RestorePersistedThreadState();
            }
            else
            {
                RefreshRecentProjects();
            }
            StatusMessage = $"Could not update project folders: {ex.Message}";
            logger.Log(AppLogLevel.Error, "project_folders_update_failed", "Could not update project folders.", exception: ex);
        }
    }

    private void CaptureReviewCommentDraft(string? projectPath, string? threadId)
    {
        if (!isRestoringReviewCommentDraft)
        {
            ComposerReviewCommentDraftStore.Capture(
                settings,
                projectPath,
                threadId,
                Git.CaptureReviewComments());
        }
    }

    private void RestoreReviewCommentDraft(string? projectPath, string? threadId)
    {
        isRestoringReviewCommentDraft = true;
        try
        {
            Git.SetReviewComments(ComposerReviewCommentDraftStore.Restore(
                settings,
                projectPath,
                threadId));
        }
        finally
        {
            isRestoringReviewCommentDraft = false;
        }
    }

    private async Task SaveReviewCommentDraftAsync()
    {
        if (isRestoringReviewCommentDraft)
        {
            return;
        }
        CaptureReviewCommentDraft(SelectedProjectPath, activeThreadId);
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            logger.Log(
                AppLogLevel.Warning,
                "review_comment_draft_save_failed",
                "Could not save the inline review comment draft.",
                exception: exception);
        }
    }

    private async Task AcknowledgeReviewCommentsAsync(
        string? projectPath,
        string? threadId,
        IReadOnlyList<GitInlineComment> capturedComments)
    {
        var capturedIds = capturedComments.Select(comment => comment.Id).ToArray();
        if (capturedIds.Length == 0)
        {
            return;
        }

        var isCurrentScope = string.Equals(activeThreadId, threadId, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(projectPath)
                ? string.IsNullOrWhiteSpace(SelectedProjectPath)
                : ProjectFolderSet.PathsEqual(projectPath, SelectedProjectPath));
        if (isCurrentScope)
        {
            isRestoringReviewCommentDraft = true;
            try
            {
                Git.RemoveReviewComments(capturedIds);
            }
            finally
            {
                isRestoringReviewCommentDraft = false;
            }
            CaptureReviewCommentDraft(projectPath, threadId);
        }
        else
        {
            ComposerReviewCommentDraftStore.Remove(settings, projectPath, threadId, capturedIds);
        }

        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            logger.Log(
                AppLogLevel.Warning,
                "review_comment_acknowledgement_save_failed",
                "Could not persist the acknowledged inline review comments.",
                exception: exception);
        }
    }

    private async Task SelectProjectAsync(string path)
    {
        CaptureAttachmentDraft(SelectedProjectPath, activeThreadId);
        CaptureReviewCommentDraft(SelectedProjectPath, activeThreadId);
        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        isRestoringAttachmentDraft = true;
        try
        {
            TaskWorkspace.ClearAttachments();
        }
        finally
        {
            isRestoringAttachmentDraft = false;
        }
        SelectedProjectPath = Path.GetFullPath(path);
        activeThreadId = null;
        activeTurnId = null;
        activeThreadLoaded = false;
        RestorePersistedThreadState();
        OnPropertyChanged(nameof(FinalResponse));
        projectWorkspaceOperations.AddRecentProject(settings, SelectedProjectPath);
        RefreshRecentProjects();
        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        await Git.RefreshAsync().ConfigureAwait(true);
        StatusMessage = IsGitRepository
            ? $"Project selected: {SelectedProjectName}"
            : "Project selected, but no Git repository was detected";
        logger.Log(
            AppLogLevel.Information,
            "project_selected",
            "A project was selected.",
            new Dictionary<string, string?> { ["path"] = SelectedProjectPath });
    }

    private Task NewThreadForCurrentScopeAsync() =>
        string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? NewGeneralThreadAsync()
            : NewProjectThreadAsync();

    private async Task NewGeneralThreadAsync()
    {
        if (!CanManageThreads() || string.IsNullOrWhiteSpace(generalWorkspacePath))
        {
            StatusMessage = generalWorkspaceError ?? "Sign in before creating a thread";
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            CaptureAttachmentDraft(SelectedProjectPath, activeThreadId);
            CaptureReviewCommentDraft(SelectedProjectPath, activeThreadId);
            SelectedProjectPath = null;
            activeThreadId = null;
            activeTurnId = null;
            activeThreadLoaded = false;
            RestorePersistedThreadState();
        }

        NewThreadWorkspaceMode = "Current checkout";
        await NewThreadAsync(ThreadScopeKey.General).ConfigureAwait(true);
    }

    private async Task NewProjectThreadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            StatusMessage = "Select a project before creating a project thread";
            return;
        }

        await NewThreadAsync(ThreadScopeKey.ForProject(SelectedProjectPath)).ConfigureAwait(true);
    }

    private async Task NewThreadAsync(ThreadScopeKey scope)
    {
        if (!CanManageThreads())
        {
            StatusMessage = generalWorkspaceError ?? "Sign in before creating a thread";
            return;
        }

        try
        {
            var workspacePath = GetWorkspacePath(scope);
            var instructionSnapshot = ResolveDefaultInstructionSnapshot();
            await EnsureHarnessSessionAsync(ResolveHarnessId(), workspacePath).ConfigureAwait(true);
            var result = await threadLifecycle.StartAsync(new ThreadStartUseCaseRequest(
                settings,
                scope,
                $"Thread {ProjectThreads.Count + 1}",
                workspacePath,
                ResolveHarnessId(),
                CreateHarnessConnectionOptions(workspacePath),
                CreateConversationStartCommand(workspacePath, instructionSnapshot, scope.ProjectPath),
                new ThreadInstructionSnapshot(instructionSnapshot.DeveloperInstructions, instructionSnapshot.BaseInstructions),
                IsTitlePlaceholder: true,
                CreateWorktree: scope.Kind == ThreadScopeKind.Project &&
                    string.Equals(NewThreadWorkspaceMode, "New worktree", StringComparison.Ordinal),
                WorktreeTaskId: $"thread-{ProjectThreads.Count + 1}")).ConfigureAwait(true);
            conversationWorkflow.MarkLoaded(result.State.ThreadId);
            RefreshProjectThreads(result.State.ThreadId);
            StatusMessage = scope.Kind == ThreadScopeKind.General
                ? "New Codex thread created in General"
                : result.Worktree is null
                    ? "New Codex thread created in the current checkout"
                    : $"New Codex thread created in worktree {result.Worktree.TaskId}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_create_failed", "Could not create a Codex thread.", exception: ex);
        }
    }

    private async Task ResumeSelectedThreadAsync()
    {
        if (!CanUseSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(SelectedThread),
                GetActiveWorkspacePath()).ConfigureAwait(true);
            var result = await threadLifecycle
                .ResumeAsync(CreateThreadResumeRequest(SelectedThread.ThreadId, GetActiveWorkspacePath()))
                .ConfigureAwait(true);
            conversationWorkflow.RegisterResumed(result.ThreadId, result.Turns);
            TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.GetSnapshot(result.ThreadId));
            activeThreadLoaded = true;
            StatusMessage = "Codex thread resumed";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_resume_failed", "Could not resume the selected thread.", exception: ex);
        }
    }

    private Task ForkSelectedThreadAsync() => ForkThreadAsync(null);

    private Task ForkConversationFromTurnAsync(string turnId) => ForkThreadAsync(turnId);

    private async Task ForkThreadAsync(string? forkPointTurnId)
    {
        var forkPoint = string.IsNullOrWhiteSpace(forkPointTurnId) || string.IsNullOrWhiteSpace(activeThreadId)
            ? null
            : conversationWorkflow.GetConversationTurn(activeThreadId, forkPointTurnId);
        if (!CanForkSelectedThread() ||
            SelectedThread is null ||
            (!string.IsNullOrWhiteSpace(forkPointTurnId) &&
             (forkPoint is null || IsTurnRunning || forkPoint.IsSuperseded || string.IsNullOrWhiteSpace(forkPoint.AssistantResponse))))
        {
            return;
        }

        try
        {
            var sourceThread = SelectedThread;
            var sourceWorkspace = GetActiveWorkspacePath();
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(sourceThread),
                sourceWorkspace).ConfigureAwait(true);
            var instructionSnapshot = ResolveInstructionSnapshot(sourceThread.ThreadId);
            var result = await threadLifecycle.ForkAsync(new ThreadForkRequest(
                settings,
                sourceThread,
                sourceWorkspace,
                CreateHarnessConnectionOptions(sourceWorkspace),
                CreateThreadForkRequest(sourceThread, sourceWorkspace, instructionSnapshot),
                new ThreadInstructionSnapshot(instructionSnapshot.DeveloperInstructions, instructionSnapshot.BaseInstructions),
                forkPointTurnId,
                sourceThread.ScopeKind == ThreadScopeKind.Project &&
                string.Equals(sourceThread.Mode, "worktree", StringComparison.OrdinalIgnoreCase))).ConfigureAwait(true);
            conversationWorkflow.RegisterCreated(result.State);
            RefreshProjectThreads(result.State.ThreadId);
            StatusMessage = string.IsNullOrWhiteSpace(forkPointTurnId)
                ? "Codex thread forked"
                : "Conversation forked from the selected response";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_fork_failed", "Could not fork the selected thread.", exception: ex);
        }
    }

    private async Task ArchiveSelectedThreadAsync()
    {
        if (!CanArchiveSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(SelectedThread),
                SelectedThread.WorkspacePath ?? SelectedThread.ProjectPath).ConfigureAwait(true);
            await Terminal.StopAndRemoveAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            await threadLifecycle.ArchiveAsync(
                settings,
                SelectedThread.ThreadId,
                CreateHarnessConnectionOptions(SelectedThread.WorkspacePath ?? SelectedThread.ProjectPath)).ConfigureAwait(true);
            StatusMessage = "Codex thread archived";
            RefreshProjectThreads(SelectedThread.ThreadId);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_archive_failed", "Could not archive the selected thread.", exception: ex);
        }
    }

    private async Task UnarchiveSelectedThreadAsync()
    {
        if (!CanUnarchiveSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(SelectedThread),
                SelectedThread.WorkspacePath ?? SelectedThread.ProjectPath).ConfigureAwait(true);
            await threadLifecycle.UnarchiveAsync(
                settings,
                SelectedThread.ThreadId,
                CreateHarnessConnectionOptions(SelectedThread.WorkspacePath ?? SelectedThread.ProjectPath)).ConfigureAwait(true);
            StatusMessage = "Codex thread unarchived";
            RefreshProjectThreads(SelectedThread.ThreadId);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_unarchive_failed", "Could not unarchive the selected thread.", exception: ex);
        }
    }

    private async Task ToggleSelectedThreadPinAsync()
    {
        if (!CanToggleSelectedThreadPin() || SelectedThread is null)
        {
            return;
        }

        try
        {
            var threadId = SelectedThread.ThreadId;
            var pinned = !SelectedThread.IsPinned;
            await threadLifecycle.SetPinnedAsync(settings, threadId, pinned).ConfigureAwait(true);
            RefreshProjectThreads(threadId);
            StatusMessage = pinned ? "Chat pinned" : "Chat unpinned";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "thread_pin_failed", "Could not update the selected chat pin.", exception: ex);
        }
    }

    private async Task RenameSelectedThreadAsync()
    {
        if (!CanRenameSelectedThread() || SelectedThread is null)
        {
            return;
        }

        var thread = SelectedThread;
        var requestedTitle = userInteractionService.PromptForText(
            "Rename chat",
            "Enter a new name for this chat.",
            thread.DisplayTitle);
        if (requestedTitle is null)
        {
            return;
        }

        var title = requestedTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusMessage = "Chat name cannot be empty";
            return;
        }
        if (string.Equals(thread.Title, title, StringComparison.Ordinal))
        {
            StatusMessage = "Chat name unchanged";
            return;
        }

        try
        {
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(thread),
                thread.WorkspacePath ?? thread.ProjectPath).ConfigureAwait(true);
            await threadLifecycle.RenameAsync(
                settings,
                thread.ThreadId,
                title,
                CreateHarnessConnectionOptions(thread.WorkspacePath ?? thread.ProjectPath)).ConfigureAwait(true);
            RefreshProjectThreads(thread.ThreadId);
            StatusMessage = "Chat renamed";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "thread_rename_failed", "Could not rename the selected chat.", exception: ex);
        }
    }

    private async Task DeleteSelectedThreadAsync()
    {
        if (!CanDeleteSelectedThread() || SelectedThread is null)
        {
            return;
        }

        var thread = SelectedThread;
        var worktreeNotice = string.Equals(thread.Mode, "worktree", StringComparison.OrdinalIgnoreCase)
            ? "\n\nIts worktree and Git branch will be preserved."
            : string.Empty;
        var confirmed = userInteractionService.ConfirmDestructiveAction(
            "Delete chat",
            $"Permanently delete \"{thread.DisplayTitle}\" from SynthiaCode?\n\nThis cannot be undone. The Codex thread will be archived before the local chat record is removed.{worktreeNotice}");
        if (!confirmed)
        {
            StatusMessage = "Chat deletion cancelled";
            return;
        }

        try
        {
            if (!thread.IsArchived)
            {
                await EnsureHarnessSessionAsync(
                    ResolveHarnessId(thread),
                    thread.WorkspacePath ?? thread.ProjectPath).ConfigureAwait(true);
            }

            await Terminal.StopAndRemoveAsync(thread.ThreadId).ConfigureAwait(true);
            await threadLifecycle.DeleteAsync(
                settings,
                thread.ThreadId,
                !thread.IsArchived,
                CreateHarnessConnectionOptions(thread.WorkspacePath ?? thread.ProjectPath)).ConfigureAwait(true);
            await followUpQueue.RemoveAsync(thread.ThreadId).ConfigureAwait(true);
            conversationWorkflow.RemoveRuntime(thread.ThreadId);
            var nextThreadId = conversationWorkflow.GetThreads(settings, thread.ScopeKey).FirstOrDefault()?.ThreadId;
            settings.ComposerAttachmentDrafts.RemoveAll(draft =>
                string.Equals(draft.ThreadId, thread.ThreadId, StringComparison.Ordinal));
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);

            activeThreadId = null;
            activeTurnId = null;
            activeThreadLoaded = false;
            RefreshProjectThreads(nextThreadId, preserveCurrentSelection: false);
            StatusMessage = "Chat deleted";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "thread_delete_failed", "Could not delete the selected chat.", exception: ex);
        }
    }

    private async Task SteerTurnAsync()
    {
        if (TaskWorkspace.FollowUpBehavior == FollowUpBehavior.Queue)
        {
            await QueueActiveFollowUpAsync().ConfigureAwait(true);
            return;
        }

        await SendSteerAsync().ConfigureAwait(true);
    }

    private Task SendAlternateFollowUpAsync() =>
        TaskWorkspace.FollowUpBehavior == FollowUpBehavior.Queue
            ? SendSteerAsync()
            : QueueActiveFollowUpAsync();

    private async Task QueueActiveFollowUpAsync()
    {
        if (!CanSteerTurn() || activeThreadId is null)
        {
            return;
        }

        var threadId = activeThreadId;
        var sourceProjectPath = SelectedProjectPath;
        try
        {
            var guidance = SteeringText.Trim();
            var attachments = TaskWorkspace.Attachments.Select(attachment => attachment.Clone()).ToList();
            var capturedComments = Git.CaptureReviewComments();
            var skillInputs = TaskWorkspace.SkillSelector.ResolveSkillInputs(guidance);
            var mutation = await followUpQueue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
                settings,
                threadId,
                guidance,
                CaptureQueuedTurnOptions(threadId, GetWorkspacePathForThread(threadId)),
                attachments,
                skillInputs,
                capturedComments)).ConfigureAwait(true);
            ApplyFollowUpQueueMutation(threadId, mutation);
            await AcknowledgeReviewCommentsAsync(sourceProjectPath, threadId, capturedComments).ConfigureAwait(true);
            TaskWorkspace.SkillSelector.ClearSelectedSkills();
            SteeringText = string.Empty;
            TaskWorkspace.ClearAttachments();
            StatusMessage = "Follow-up queued for the next turn";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "follow_up_queue_failed", "Could not queue the follow-up.", exception: ex);
        }
    }

    private async Task PersistSelectedFollowUpQueueAsync(IReadOnlyList<QueuedFollowUpSnapshot> snapshots)
    {
        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            return;
        }

        var mutation = await followUpQueue
            .ReplaceAsync(settings, activeThreadId, snapshots)
            .ConfigureAwait(true);
        ApplyFollowUpQueueMutation(activeThreadId, mutation);
        RaiseThreadCommandStates();
    }

    private async Task SendQueuedFollowUpNowAsync(string followUpId)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(activeThreadId) ||
            !followUpQueue.HasQueue(activeThreadId))
        {
            return;
        }

        var threadId = activeThreadId;
        var item = followUpQueue.Get(threadId, followUpId);
        if (item is null)
        {
            return;
        }

        if (IsTurnRunning)
        {
            if (!IsHarnessConnected(FindThread(threadId)) || string.IsNullOrWhiteSpace(activeTurnId))
            {
                StatusMessage = "The active turn is not ready for steering";
                return;
            }

            try
            {
                var turnId = activeTurnId;
                var effectiveGuidance = GitInlineCommentPromptFormatter.AppendToPrompt(
                    item.Text,
                    item.ReviewComments);
                var mutation = await followUpQueue.SteerAsync(
                    settings,
                    threadId,
                    item.Id,
                    CreateHarnessConnectionOptions(item.Options.WorkspacePath),
                    new SteerTurnCommand(
                        GetConversationAddress(threadId),
                        turnId,
                        attachmentDraftService.BuildHarnessPromptInputs(
                            effectiveGuidance, item.Attachments, item.Options.WorkspacePath,
                            ResolveModel(item.Options.Model), item.SkillInputs,
                            GetQueuedWorkspaceRoots(threadId, item.Options)))).ConfigureAwait(true);
                ApplyFollowUpQueueMutation(threadId, mutation);
                TaskWorkspace.NotifyQueuedFollowUpsChanged();
                TaskWorkspace.NotifyResponseChanged();
                StatusMessage = "Queued follow-up steered into the active turn";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not steer this message. It remains queued. {ex.Message}";
                logger.Log(AppLogLevel.Warning, "queued_follow_up_steer_failed", "A queued item could not steer the active turn.", exception: ex);
            }
            return;
        }

        if (!followUpQueue.IsFirst(threadId, item.Id))
        {
            StatusMessage = "Move this follow-up to the top before sending it";
            return;
        }

        if (item.State == QueuedFollowUpState.NeedsAttention)
        {
            var mutation = await followUpQueue
                .MarkPendingAsync(settings, threadId, item.Id)
                .ConfigureAwait(true);
            ApplyFollowUpQueueMutation(threadId, mutation);
        }
        await TryDrainFollowUpQueueAsync(threadId).ConfigureAwait(true);
    }

    private async Task SendSteerAsync()
    {
        if (!CanSteerTurn() || activeThreadId is null || activeTurnId is null)
        {
            return;
        }

        var threadId = activeThreadId;
        var turnId = activeTurnId;
        var sourceProjectPath = SelectedProjectPath;
        try
        {
            var guidance = SteeringText.Trim();
            var attachments = TaskWorkspace.Attachments.Select(attachment => attachment.Clone()).ToList();
            var capturedComments = Git.CaptureReviewComments();
            var effectiveGuidance = GitInlineCommentPromptFormatter.AppendToPrompt(
                guidance,
                capturedComments);
            await turnExecution.SteerAsync(
                threadId,
                CreateHarnessConnectionOptions(GetActiveWorkspacePath()),
                new SteerTurnCommand(
                    GetConversationAddress(threadId),
                    turnId,
                    attachmentDraftService.BuildHarnessPromptInputs(
                        effectiveGuidance, attachments, GetActiveWorkspacePath(), TaskWorkspace.SelectedModel,
                        TaskWorkspace.SkillSelector.ResolveSkillInputs(guidance),
                        GetActiveWorkspaceRoots())),
                effectiveGuidance).ConfigureAwait(true);
            await AcknowledgeReviewCommentsAsync(sourceProjectPath, threadId, capturedComments).ConfigureAwait(true);
            TaskWorkspace.NotifyResponseChanged();
            TaskWorkspace.SkillSelector.ClearSelectedSkills();
            SteeringText = string.Empty;
            TaskWorkspace.ClearAttachments();
            StatusMessage = "Steering sent to active turn";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "turn_steer_failed", "Could not steer the active turn.", exception: ex);
        }
    }


    private async Task RemoveSelectedWorktreeAsync()
    {
        if (!CanRemoveSelectedWorktree() || SelectedThread?.WorkspacePath is null || SelectedProjectPath is null)
        {
            return;
        }

        var thread = SelectedThread;
        var worktreePath = Path.GetFullPath(thread.WorkspacePath);
        var confirmed = userInteractionService.ConfirmDestructiveAction(
            "Remove assistant worktree",
            $"Remove this assistant-created worktree?\n\n{worktreePath}\n\nGit will refuse if it contains uncommitted changes. The branch will be preserved.");
        if (!confirmed)
        {
            StatusMessage = "Worktree cleanup cancelled";
            return;
        }

        try
        {
            await Terminal.StopAndRemoveAsync(thread.ThreadId).ConfigureAwait(true);
            await threadLifecycle.RemoveWorktreeAsync(settings, thread, SelectedProjectPath).ConfigureAwait(true);
            thread.Mode = "worktree-removed";
            thread.TurnStatus = "Workspace removed";
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(ActiveWorkspaceLabel));
            removeWorktreeCommand.RaiseCanExecuteChanged();
            StatusMessage = "Assistant worktree removed; its Git branch was preserved";
            await Git.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "assistant_worktree_remove_failed", "Could not remove the selected assistant worktree.", exception: ex);
        }
    }

    public void UpdateViewportWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return;
        }

        viewportWidth = width;
        IsCompactLayout = width < 1100;
        RaiseShellVisibilityProperties();
        if (IsCompactLayout && IsProjectRailOpen && IsDetailsPaneOpen)
        {
            IsDetailsPaneOpen = false;
            settings.IsDetailsPaneOpen = false;
            _ = SaveLayoutSelectionAsync();
        }
    }

    private void ToggleProjectRail()
    {
        IsProjectRailOpen = !IsProjectRailOpen;
        if (IsProjectRailOpen && IsCompactLayout)
        {
            IsDetailsPaneOpen = false;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = IsDetailsPaneOpen;
        _ = SaveLayoutSelectionAsync();
    }

    private void ToggleDetailsPane()
    {
        IsDetailsPaneOpen = !IsDetailsPaneOpen;
        if (IsDetailsPaneOpen && IsCompactLayout)
        {
            IsProjectRailOpen = false;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = IsDetailsPaneOpen;
        _ = SaveLayoutSelectionAsync();
    }

    private void OpenSettings()
    {
        SelectedInspectorTabIndex = 1;
        IsDetailsPaneOpen = true;
        if (IsCompactLayout)
        {
            IsProjectRailOpen = false;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = true;
        _ = SaveLayoutSelectionAsync();
        _ = CodexConfiguration.RefreshIfCleanAsync(appServerWarmUpCancellation.Token);
        if (appServerSessionCoordinator.State == AppServerSessionState.Connected)
        {
            var policyCwd = GetActiveWorkspacePathIfAvailable();
            if (!executionPolicyLoaded || !string.Equals(executionPolicyCwd, policyCwd, StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshExecutionPolicyAsync(policyCwd, appServerWarmUpCancellation.Token);
            }
        }
    }

    private void OpenChanges()
    {
        SelectedInspectorTabIndex = 0;
        IsDetailsPaneOpen = true;
        if (IsCompactLayout)
        {
            IsProjectRailOpen = false;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = true;
        _ = SaveLayoutSelectionAsync();
    }

    private void DismissShellOverlay()
    {
        if (IsInspectorOverlayVisible)
        {
            IsDetailsPaneOpen = false;
        }
        else if (IsProjectRailOverlayVisible)
        {
            IsProjectRailOpen = false;
        }
        else
        {
            return;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = IsDetailsPaneOpen;
        _ = SaveLayoutSelectionAsync();
    }

    private void RaiseShellVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsMediumLayout));
        OnPropertyChanged(nameof(IsWideLayout));
        OnPropertyChanged(nameof(IsProjectRailPersistentVisible));
        OnPropertyChanged(nameof(IsProjectRailOverlayVisible));
        OnPropertyChanged(nameof(IsInspectorPersistentVisible));
        OnPropertyChanged(nameof(IsInspectorOverlayVisible));
        OnPropertyChanged(nameof(IsShellOverlayVisible));
    }

    private async Task SaveLayoutSelectionAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "layout_save_failed", "Could not save the selected layout.", exception: ex);
        }
    }

    private string? GetActiveWorkspacePathIfAvailable()
    {
        var path = SelectedThread?.WorkspacePath ?? SelectedProjectPath ?? generalWorkspacePath;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private TerminalContext CreateTerminalContext()
    {
        var workspacePath = GetActiveWorkspacePathIfAvailable();
        if (!string.IsNullOrWhiteSpace(SelectedThread?.ThreadId))
        {
            return new TerminalContext(SelectedThread.ThreadId, workspacePath);
        }

        var key = string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? "scope:general"
            : $"project:{Path.GetFullPath(SelectedProjectPath)}";
        return new TerminalContext(key, workspacePath);
    }

    private GitContext CreateGitContext() => new(
        SelectedProjectPath,
        GetActiveWorkspacePathIfAvailable(),
        settings.RecentProjects.FirstOrDefault(project =>
            ProjectFolderSet.PathsEqual(project.Path, SelectedProjectPath))?.FolderPaths,
        IsGeneral: string.IsNullOrWhiteSpace(SelectedProjectPath));

    private void LoadInstructionSettings()
    {
        settings.CustomDeveloperInstructions ??= string.Empty;
        settings.CustomBaseInstructions ??= string.Empty;
        developerInstructionsEnabled = settings.CustomDeveloperInstructionsEnabled;
        developerInstructions = settings.CustomDeveloperInstructions;
        baseInstructionsEnabled = settings.CustomBaseInstructionsEnabled;
        baseInstructions = settings.CustomBaseInstructions;
        instructionSettingsInitialized = true;
        OnPropertyChanged(nameof(DeveloperInstructionsEnabled));
        OnPropertyChanged(nameof(DeveloperInstructions));
        OnPropertyChanged(nameof(BaseInstructionsEnabled));
        OnPropertyChanged(nameof(BaseInstructions));
        RefreshInstructionSettingsState();
    }

    private bool CanSaveInstructionSettings() =>
        !IsShuttingDown &&
        instructionSettingsInitialized &&
        HasInstructionSettingsChanges &&
        string.IsNullOrEmpty(GetInstructionSettingsValidationMessage());

    private async Task SaveInstructionSettingsAsync()
    {
        var validationMessage = GetInstructionSettingsValidationMessage();
        if (!string.IsNullOrEmpty(validationMessage))
        {
            InstructionSettingsValidationMessage = validationMessage;
            return;
        }

        var previousDeveloperEnabled = settings.CustomDeveloperInstructionsEnabled;
        var previousDeveloperInstructions = settings.CustomDeveloperInstructions;
        var previousBaseEnabled = settings.CustomBaseInstructionsEnabled;
        var previousBaseInstructions = settings.CustomBaseInstructions;
        settings.CustomDeveloperInstructionsEnabled = DeveloperInstructionsEnabled;
        settings.CustomDeveloperInstructions = DeveloperInstructions;
        settings.CustomBaseInstructionsEnabled = BaseInstructionsEnabled;
        settings.CustomBaseInstructions = BaseInstructions;

        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
            StatusMessage = "Codex instruction defaults saved for future threads";
        }
        catch (Exception ex)
        {
            settings.CustomDeveloperInstructionsEnabled = previousDeveloperEnabled;
            settings.CustomDeveloperInstructions = previousDeveloperInstructions;
            settings.CustomBaseInstructionsEnabled = previousBaseEnabled;
            settings.CustomBaseInstructions = previousBaseInstructions;
            StatusMessage = "Could not save Codex instruction defaults";
            logger.Log(
                AppLogLevel.Warning,
                "instruction_settings_save_failed",
                "Could not save custom Codex instruction defaults.",
                exception: ex);
        }
        finally
        {
            RefreshInstructionSettingsState();
        }
    }

    private void ResetInstructionSettings()
    {
        DeveloperInstructionsEnabled = false;
        DeveloperInstructions = string.Empty;
        BaseInstructionsEnabled = false;
        BaseInstructions = string.Empty;
    }

    private void RefreshInstructionSettingsState()
    {
        if (!instructionSettingsInitialized)
        {
            return;
        }

        InstructionSettingsValidationMessage = GetInstructionSettingsValidationMessage();
        OnPropertyChanged(nameof(HasInstructionSettingsChanges));
        saveInstructionSettingsCommand.RaiseCanExecuteChanged();
    }

    private string GetInstructionSettingsValidationMessage()
    {
        if (DeveloperInstructionsEnabled && string.IsNullOrWhiteSpace(DeveloperInstructions))
        {
            return "Enter developer instructions or turn off the override.";
        }

        if (BaseInstructionsEnabled && string.IsNullOrWhiteSpace(BaseInstructions))
        {
            return "Enter base instructions or turn off the advanced override.";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(DeveloperInstructions) > MaximumInstructionBytes)
        {
            return "Developer instructions must be 64 KiB or smaller.";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(BaseInstructions) > MaximumInstructionBytes)
        {
            return "Base instructions must be 64 KiB or smaller.";
        }

        return string.Empty;
    }

    private async Task SaveThemeSelectionAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "theme_save_failed", "Could not save the selected theme.", exception: ex);
        }
    }

    private static string NormalizeTheme(string? theme) =>
        theme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "System"
        };

    private async Task SubmitPromptAsync()
    {
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

        if (IsTurnRunning)
        {
            StatusMessage = $"A {ResolveHarnessName(SelectedThread)} turn is already running";
            return;
        }

        if (string.IsNullOrWhiteSpace(PromptText) && !TaskWorkspace.HasAttachments && !Git.HasReviewComments)
        {
            StatusMessage = "Enter a prompt, add an inline comment, or attach a file before starting a task";
            return;
        }

        if (!TaskWorkspace.CanSubmitAttachments)
        {
            StatusMessage = TaskWorkspace.AttachmentValidationMessage;
            return;
        }

        if (!IsHarnessReady(SelectedThread))
        {
            StatusMessage = $"The {ResolveHarnessName(SelectedThread)} harness is unavailable";
            return;
        }

        if (!Supports(HarnessCapability.StartTurn, SelectedThread))
        {
            StatusMessage = $"{ResolveHarnessName(SelectedThread)} cannot start turns";
            return;
        }

        StatusMessage = $"Starting {ResolveHarnessName(SelectedThread)} task";
        var sourceProjectPath = SelectedProjectPath;

        try
        {
            var submittedPrompt = PromptText.Trim();
            var submittedImages = TaskWorkspace.Attachments.Select(image => image.Clone()).ToList();
            var capturedComments = Git.CaptureReviewComments();
            var effectivePrompt = GitInlineCommentPromptFormatter.AppendToPrompt(
                submittedPrompt,
                capturedComments);
            TaskWorkspace.SubmittedPrompt = effectivePrompt;
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(SelectedThread),
                GetActiveWorkspacePath()).ConfigureAwait(true);
            activeThreadId = await EnsureActiveThreadAsync().ConfigureAwait(true);
            var submissionThreadId = activeThreadId;
            var persistedThread = settings.ProjectThreads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, activeThreadId, StringComparison.Ordinal));
            var titlePrompt = string.IsNullOrWhiteSpace(submittedPrompt)
                ? "Address inline review comments"
                : submittedPrompt;
            var automaticTitle = persistedThread?.IsTitlePlaceholder == true
                ? CreateAutomaticThreadTitle(titlePrompt, submittedImages)
                : null;
            var workspacePath = GetActiveWorkspacePath();
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            settings.LastServiceTierOverride = ToSettingsValue(TaskWorkspace.ServiceTierSelection);
            var result = await turnExecution.StartAsync(new TurnExecutionRequest(
                settings,
                activeThreadId,
                GetConversationAddress(activeThreadId),
                effectivePrompt,
                submittedImages,
                CreateHarnessConnectionOptions(workspacePath),
                CreateHarnessTurnStartCommand(activeThreadId, effectivePrompt, submittedImages, workspacePath),
                automaticTitle,
                snapshot => InvokeOnCapturedSynchronizationContext(
                    () => TaskWorkspace.ApplyConversationSnapshot(snapshot)),
                started => InvokeOnCapturedSynchronizationContext(() =>
                {
                    TaskWorkspace.ApplyConversationSnapshot(started.Snapshot);
                    if (started.Status == CodexTurnStatus.Running)
                    {
                        UpdateThreadActivity(started.ThreadId, isRunning: true, "Running");
                        IsTurnRunning = true;
                        activeTurnId = started.TurnId;
                    }
                    else
                    {
                        activeTurnId = null;
                        IsTurnRunning = false;
                    }
                    cancelTurnCommand.RaiseCanExecuteChanged();
                    _ = AcknowledgeReviewCommentsAsync(sourceProjectPath, submissionThreadId, capturedComments);
                    TaskWorkspace.ClearAttachments();
                    StatusMessage = started.Status == CodexTurnStatus.Running
                        ? "Codex turn running"
                        : $"Codex turn {started.Status.ToString().ToLowerInvariant()}";
                }))).ConfigureAwait(true);
            TaskWorkspace.SkillSelector.ClearSelectedSkills();
            TaskWorkspace.ApplyConversationSnapshot(result.Snapshot);
            if (SelectedThread is not null)
            {
                SelectedThread.Preview = persistedThread?.Preview ?? titlePrompt;
            }
            if (result.Status == CodexTurnStatus.Running)
            {
                UpdateThreadActivity(activeThreadId, isRunning: true, "Running");
                IsTurnRunning = true;
                activeTurnId = result.TurnId;
            }
            else
            {
                activeTurnId = null;
                IsTurnRunning = false;
            }
            cancelTurnCommand.RaiseCanExecuteChanged();
            TaskWorkspace.ClearAttachments();
            StatusMessage = result.Status == CodexTurnStatus.Running
                ? "Codex turn running"
                : $"Codex turn {result.Status.ToString().ToLowerInvariant()}";
            if (result.AutomaticTitleApplied)
            {
                RefreshProjectThreads(activeThreadId);
            }
            if (!string.IsNullOrWhiteSpace(result.AutomaticTitleError))
            {
                logger.Log(
                    AppLogLevel.Warning,
                    "thread_auto_rename_failed",
                    "Could not automatically name the chat from its first message.",
                    new Dictionary<string, string?>
                    {
                        ["threadId"] = activeThreadId,
                        ["error"] = result.AutomaticTitleError
                    });
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(activeThreadId))
            {
                TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.GetSnapshot(activeThreadId));
            }
            IsTurnRunning = false;
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_task_start_failed", "Could not start Codex task.", exception: ex);
        }
    }

    private async Task StartCodeReviewAsync()
    {
        if (!CanStartCodeReview())
        {
            StatusMessage = IsTurnRunning
                ? "Wait for the active Codex turn to finish before starting a review"
                : "Code review requires an available Codex project chat";
            return;
        }

        try
        {
            var workspacePath = GetActiveWorkspacePath();
            StatusMessage = "Loading code review targets";
            var catalog = await gitService.GetReviewCatalogAsync(workspacePath).ConfigureAwait(true);
            var target = userInteractionService.SelectCodeReviewTarget(catalog);
            if (target is null)
            {
                StatusMessage = "Code review canceled";
                return;
            }

            StatusMessage = "Starting Codex code review";
            await EnsureHarnessSessionAsync(HarnessId.Codex, workspacePath).ConfigureAwait(true);
            activeThreadId = await EnsureActiveThreadAsync().ConfigureAwait(true);
            TaskWorkspace.SubmittedPrompt = target.DisplayLabel;
            var persistedThread = settings.ProjectThreads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, activeThreadId, StringComparison.Ordinal));
            if (persistedThread is not null)
            {
                persistedThread.Preview = target.DisplayLabel;
            }

            var result = await codeReview.StartAsync(new CodeReviewExecutionRequest(
                activeThreadId,
                target,
                snapshot => InvokeOnCapturedSynchronizationContext(
                    () => TaskWorkspace.ApplyConversationSnapshot(snapshot)),
                started => InvokeOnCapturedSynchronizationContext(() =>
                {
                    TaskWorkspace.ApplyConversationSnapshot(started.Snapshot);
                    if (started.Status == CodexTurnStatus.Running)
                    {
                        UpdateThreadActivity(started.ThreadId, isRunning: true, "Reviewing");
                        IsTurnRunning = true;
                        activeTurnId = started.TurnId;
                    }
                    else
                    {
                        IsTurnRunning = false;
                        activeTurnId = null;
                    }
                    cancelTurnCommand.RaiseCanExecuteChanged();
                    StatusMessage = started.Status == CodexTurnStatus.Running
                        ? "Code review running"
                        : $"Code review {started.Status.ToString().ToLowerInvariant()}";
                }))).ConfigureAwait(true);

            TaskWorkspace.ApplyConversationSnapshot(result.Snapshot);
            if (SelectedThread is not null)
            {
                SelectedThread.Preview = target.DisplayLabel;
            }
            if (result.Status == CodexTurnStatus.Running)
            {
                UpdateThreadActivity(result.ThreadId, isRunning: true, "Reviewing");
                IsTurnRunning = true;
                activeTurnId = result.TurnId;
            }
            else
            {
                IsTurnRunning = false;
                activeTurnId = null;
            }
            if (string.Equals(TaskWorkspace.Prompt.Trim(), "/review", StringComparison.OrdinalIgnoreCase))
            {
                TaskWorkspace.Prompt = string.Empty;
            }
            cancelTurnCommand.RaiseCanExecuteChanged();
            TaskWorkspace.RaiseCommandStates();
            StatusMessage = result.Status == CodexTurnStatus.Running
                ? "Code review running"
                : $"Code review {result.Status.ToString().ToLowerInvariant()}";
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(activeThreadId))
            {
                TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.GetSnapshot(activeThreadId));
            }
            IsTurnRunning = false;
            activeTurnId = null;
            TaskWorkspace.RaiseCommandStates();
            StatusMessage = ex.Message;
            logger.Log(
                AppLogLevel.Error,
                "codex_code_review_start_failed",
                "Could not start the Codex code review.",
                exception: ex);
        }
    }


    private static string CreateAutomaticThreadTitle(
        string submittedPrompt,
        IReadOnlyList<AttachmentReference> submittedAttachments)
    {
        var source = string.IsNullOrWhiteSpace(submittedPrompt)
            ? submittedAttachments.FirstOrDefault(attachment => !string.IsNullOrWhiteSpace(attachment.DisplayName))?.DisplayName
                ?? "Attachment request"
            : submittedPrompt;
        return string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<bool> EditPromptAsync(CodexConversationTurn sourceTurn, string editedPrompt)
    {
        if (IsShuttingDown || IsTurnRunning || sourceTurn.IsSuperseded)
        {
            StatusMessage = IsShuttingDown ? "Application is closing" : "Wait for the active Codex turn to finish before editing a prompt";
            return false;
        }
        if (string.IsNullOrWhiteSpace(editedPrompt) ||
            string.Equals(editedPrompt.Trim(), sourceTurn.UserPrompt, StringComparison.Ordinal))
        {
            StatusMessage = "Change the prompt before resubmitting it";
            return false;
        }
        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before editing a prompt";
            return false;
        }
        if (currentAuth.Readiness is AuthReadiness.Unavailable or AuthReadiness.NotSignedIn)
        {
            StatusMessage = "Sign in with Codex before editing a prompt";
            return false;
        }
        var threadId = activeThreadId;
        if (string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(sourceTurn.TurnId) ||
            !TaskWorkspace.ConversationTurns.Any(turn => string.Equals(turn.TurnId, sourceTurn.TurnId, StringComparison.Ordinal)))
        {
            StatusMessage = "The prompt is no longer part of the selected thread";
            return false;
        }

        var rollbackCount = conversationWorkflow.GetActiveRollbackTurnCount(threadId, sourceTurn.TurnId);
        if (rollbackCount < 1)
        {
            StatusMessage = "The selected prompt cannot be edited";
            return false;
        }

        var submittedPrompt = editedPrompt.Trim();
        var submittedAttachments = sourceTurn.UserAttachments.Select(attachment => attachment.Clone()).ToList();
        var workspacePath = GetActiveWorkspacePath();
        TurnEditExecutionResult result;
        try
        {
            var startRequest = CreateHarnessTurnStartCommand(
                threadId,
                submittedPrompt,
                submittedAttachments,
                workspacePath);
            StatusMessage = "Rewinding Codex thread for edited prompt";
            await EnsureHarnessSessionAsync(
                ResolveHarnessId(FindThread(threadId)),
                workspacePath).ConfigureAwait(true);
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            settings.LastServiceTierOverride = ToSettingsValue(TaskWorkspace.ServiceTierSelection);
            result = await turnExecution.EditAsync(new TurnEditExecutionRequest(
                settings,
                threadId,
                GetConversationAddress(threadId),
                sourceTurn.TurnId,
                rollbackCount,
                submittedPrompt,
                submittedAttachments,
                CreateHarnessConnectionOptions(workspacePath),
                startRequest)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(
                AppLogLevel.Error,
                "prompt_edit_failed",
                "Could not edit and resubmit the selected prompt.",
                exception: ex);
            return false;
        }

        var isSelectedAfterStart = string.Equals(activeThreadId, threadId, StringComparison.Ordinal);
        if (isSelectedAfterStart)
        {
            TaskWorkspace.ApplyConversationSnapshot(result.Snapshot);
            TaskWorkspace.SubmittedPrompt = submittedPrompt;
            TaskWorkspace.NotifyResponseChanged();
            if (SelectedThread is not null)
            {
                SelectedThread.Preview = submittedPrompt;
            }
        }

        if (result.Error is not null)
        {
            if (isSelectedAfterStart)
            {
                IsTurnRunning = false;
            }
            StatusMessage = result.Error.Message;
            logger.Log(
                AppLogLevel.Error,
                "prompt_edit_failed",
                "Could not edit and resubmit the selected prompt.",
                exception: result.Error);
            return result.StateCommitted;
        }

        var turnStatus = result.Status
            ?? throw new InvalidOperationException("The edited turn did not return a status.");
        if (turnStatus == CodexTurnStatus.Running)
        {
            UpdateThreadActivity(threadId, isRunning: true, "Running");
            if (isSelectedAfterStart)
            {
                IsTurnRunning = true;
                activeTurnId = result.TurnId;
            }
        }
        else if (isSelectedAfterStart)
        {
            activeTurnId = null;
            IsTurnRunning = false;
        }
        cancelTurnCommand.RaiseCanExecuteChanged();
        StatusMessage = turnStatus == CodexTurnStatus.Running
            ? "Edited prompt running"
            : $"Edited prompt {turnStatus.ToString().ToLowerInvariant()}";
        return true;
    }

    private async Task<string> EnsureActiveThreadAsync()
    {
        var scope = GetCurrentScope();
        var workspacePath = GetWorkspacePath(scope);
        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            var instructionSnapshot = ResolveDefaultInstructionSnapshot();
            var started = await threadLifecycle.StartAsync(new ThreadStartUseCaseRequest(
                settings,
                scope,
                $"Thread {ProjectThreads.Count + 1}",
                workspacePath,
                ResolveHarnessId(),
                CreateHarnessConnectionOptions(workspacePath),
                CreateConversationStartCommand(workspacePath, instructionSnapshot, scope.ProjectPath),
                new ThreadInstructionSnapshot(instructionSnapshot.DeveloperInstructions, instructionSnapshot.BaseInstructions),
                IsTitlePlaceholder: true,
                CreateWorktree: scope.Kind == ThreadScopeKind.Project &&
                    string.Equals(NewThreadWorkspaceMode, "New worktree", StringComparison.Ordinal),
                WorktreeTaskId: $"thread-{ProjectThreads.Count + 1}")).ConfigureAwait(true);
            RefreshProjectThreads(started.State.ThreadId);
            conversationWorkflow.MarkLoaded(started.State.ThreadId);
            activeThreadLoaded = true;
            return started.State.ThreadId;
        }

        if (activeThreadLoaded)
        {
            return activeThreadId;
        }

        var previousThreadId = activeThreadId;
        var existingInstructionSnapshot = ResolveInstructionSnapshot(previousThreadId);
        var activated = await threadLifecycle.ResumeOrReplaceAsync(new ThreadActivationUseCaseRequest(
            settings,
            scope,
            workspacePath,
            ResolveHarnessId(FindThread(previousThreadId)),
            CreateHarnessConnectionOptions(workspacePath),
            CreateThreadResumeRequest(previousThreadId, GetActiveWorkspacePath()),
            CreateConversationStartCommand(workspacePath, existingInstructionSnapshot, scope.ProjectPath),
            new ThreadInstructionSnapshot(
                existingInstructionSnapshot.DeveloperInstructions,
                existingInstructionSnapshot.BaseInstructions),
            $"Thread {ProjectThreads.Count + 1}")).ConfigureAwait(true);

        if (activated.ReplacedThread)
        {
            logger.Log(
                AppLogLevel.Warning,
                "codex_thread_resume_failed",
                "Could not resume persisted Codex thread; started a new thread.",
                new Dictionary<string, string?> { ["threadId"] = previousThreadId },
                activated.ResumeError);
            RefreshProjectThreads(activated.ThreadId);
            StatusMessage = "Previous thread could not be resumed; started a new Codex thread";
        }
        else
        {
            conversationWorkflow.RegisterResumed(activated.ThreadId, activated.Turns);
            TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.GetSnapshot(activated.ThreadId));
            StatusMessage = "Codex thread resumed";
        }

        conversationWorkflow.MarkLoaded(activated.ThreadId);
        activeThreadLoaded = true;
        return activated.ThreadId;
    }

    private async Task CancelTurnAsync()
    {
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

        if (!CanCancelTurn() || string.IsNullOrWhiteSpace(activeThreadId) || string.IsNullOrWhiteSpace(activeTurnId))
        {
            StatusMessage = "No active turn to cancel";
            return;
        }

        try
        {
            await turnExecution.CancelAsync(
                CreateHarnessConnectionOptions(GetActiveWorkspacePath()),
                GetConversationAddress(activeThreadId),
                activeTurnId).ConfigureAwait(true);
            UpdateThreadActivity(activeThreadId, isRunning: true, "Cancelling");
            StatusMessage = "Cancellation requested";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_turn_cancel_failed", "Could not cancel Codex turn.", exception: ex);
        }
    }

    private Task RequestApplicationExitAsync()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        return shutdownTask;
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var shutdownTimer = System.Diagnostics.Stopwatch.StartNew();
        var activeTurnsAtStart = conversationWorkflow.ActiveTurnCount > 0
            ? conversationWorkflow.ActiveTurnCount
            : IsTurnRunning ? 1 : 0;
        var terminalSessionsAtStart = Terminal.SessionCount;
        IsShuttingDown = true;
        StatusMessage = "Closing application";

        appServerWarmUpCancellation.Cancel();
        if (appServerWarmUpTask is not null)
        {
            try
            {
                await appServerWarmUpTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await TryCancelRunningTurnForShutdownAsync(cancellationToken).ConfigureAwait(true);
        await Terminal.ShutdownAsync().ConfigureAwait(true);
        await TaskWorkspace.DisposeAsync().ConfigureAwait(true);
        ApprovalQueue.Clear();
        appServerSessionCoordinator.ServerRequestReceived -= OnServerRequestReceived;
        harnessRuntimeCoordinator.EventReceived -= OnHarnessEventReceived;
        appServerSessionCoordinator.FlushNotifications();
        await Skills.DisposeAsync().ConfigureAwait(true);
        await followUpQueue.DisposeAsync().ConfigureAwait(true);
        await harnessRuntimeCoordinator.DisposeAsync().ConfigureAwait(true);
        await appServerSessionCoordinator.DisposeAsync().ConfigureAwait(true);
        await SaveActiveThreadStateAsync().ConfigureAwait(true);

        IsTurnRunning = false;
        activeTurnId = null;
        conversationWorkflow.ClearRuntimeState();
        StatusMessage = "Application closed";
        var notificationMetrics = appServerSessionCoordinator.NotificationMetrics;
        logger.Log(
            AppLogLevel.Information,
            "shutdown_completed",
            "Application shutdown completed.",
            new Dictionary<string, string?>
            {
                ["elapsedMilliseconds"] = shutdownTimer.ElapsedMilliseconds.ToString(),
                ["activeTurnsAtStart"] = activeTurnsAtStart.ToString(),
                ["terminalSessionsAtStart"] = terminalSessionsAtStart.ToString(),
                ["receivedNotifications"] = notificationMetrics.ReceivedCount.ToString(),
                ["emittedNotifications"] = notificationMetrics.EmittedCount.ToString()
            });
    }

    private async Task TryCancelRunningTurnForShutdownAsync(CancellationToken cancellationToken)
    {
        if (!conversationWorkflow.SnapshotRunningThreadIds().Any(threadId =>
                IsHarnessConnected(FindThread(threadId))) &&
            (string.IsNullOrWhiteSpace(activeThreadId) ||
             !IsHarnessConnected(FindThread(activeThreadId))))
        {
            return;
        }

        var turns = conversationWorkflow.ActiveTurnCount > 0
            ? conversationWorkflow.SnapshotActiveTurns()
            : !string.IsNullOrWhiteSpace(activeThreadId) && !string.IsNullOrWhiteSpace(activeTurnId)
                ? [new KeyValuePair<string, string>(activeThreadId, activeTurnId)]
                : [];

        foreach (var turn in turns)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await turnExecution.CancelAsync(
                    CreateHarnessConnectionOptions(GetWorkspacePathForThread(turn.Key)),
                    GetConversationAddress(turn.Key),
                    turn.Value,
                    timeout.Token).ConfigureAwait(true);
                conversationWorkflow.RegisterTurnFinished(turn.Key);
                StatusMessage = "Cancellation requested";
            }
            catch (OperationCanceledException ex)
            {
                logger.Log(AppLogLevel.Warning, "shutdown_cancel_turn_timed_out", "Timed out while cancelling an active turn during shutdown.", exception: ex);
            }
            catch (Exception ex)
            {
                logger.Log(AppLogLevel.Warning, "shutdown_cancel_turn_failed", "Could not cancel an active turn during shutdown.", exception: ex);
            }
        }

        IsTurnRunning = false;
    }

    private async Task LoadModelOptionsAsync()
    {
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

        if (!IsHarnessReady(SelectedThread))
        {
            StatusMessage = $"The {ResolveHarnessName(SelectedThread)} harness is unavailable";
            return;
        }

        if (!Supports(HarnessCapability.ModelCatalog, SelectedThread))
        {
            StatusMessage = $"{ResolveHarnessName(SelectedThread)} does not provide a model catalog";
            return;
        }

        loadModelsCommand.RaiseCanExecuteChanged();
        StatusMessage = $"Loading {ResolveHarnessName(SelectedThread)} models";
        TaskWorkspace.SetModelCatalogLoading();

        try
        {
            var harnessId = ResolveHarnessId(SelectedThread);
            var session = await harnessRuntimeCoordinator.GetOrConnectAsync(
                harnessId,
                CreateHarnessConnectionOptions(GetActiveWorkspacePathIfAvailable()),
                appServerWarmUpCancellation.Token).ConfigureAwait(true);
            CodexAccountInfo? account = null;
            if (harnessId == HarnessId.Codex)
            {
                try
                {
                    account = (await appServerSessionCoordinator.ReadAccountAsync().ConfigureAwait(true)).Account;
                }
                catch (Exception ex)
                {
                    logger.Log(
                        AppLogLevel.Warning,
                        "codex_model_account_read_failed",
                        "Could not read account context while loading the model catalog.",
                        exception: ex);
                }
            }
            var models = await session
                .RequireFeature<IModelCatalogFeature>(HarnessCapability.ModelCatalog)
                .ListModelsAsync(appServerWarmUpCancellation.Token)
                .ConfigureAwait(true);
            TaskWorkspace.ApplyModelCatalog(models.Select(ToPresentationModel).ToArray(), account);

            StatusMessage = TaskWorkspace.ModelCatalog.Count == 0
                ? $"No {ResolveHarnessName(SelectedThread)} models returned"
                : $"Loaded {TaskWorkspace.ModelCatalog.Count} {ResolveHarnessName(SelectedThread)} models";
        }
        catch (Exception ex)
        {
            TaskWorkspace.SetModelCatalogError(ex.Message);
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "codex_model_list_failed", "Could not load Codex model list.", exception: ex);
        }
    }

    private static string? NormalizeOverride(string? value) => CodexTurnRequestFactory.NormalizeOverride(value);

    private CodexModelOption? ResolveModel(string? model) => string.IsNullOrWhiteSpace(model)
        ? TaskWorkspace.SelectedModel
        : TaskWorkspace.ModelCatalog.FirstOrDefault(option =>
            string.Equals(option.Model, model, StringComparison.OrdinalIgnoreCase));

    private static CodexModelOption ToPresentationModel(HarnessModelDescriptor model)
    {
        var reasoning = model.Options.FirstOrDefault(option =>
            string.Equals(option.Id, "reasoning-effort", StringComparison.OrdinalIgnoreCase));
        var reasoningOptions = (reasoning?.Choices ?? [])
            .Select(choice => (Choice: choice, Effort: ParseReasoningEffort(choice.Id)))
            .Where(item => item.Effort is not null)
            .Select(item => new CodexReasoningOption(item.Effort!.Value, item.Choice.Description))
            .ToArray();
        var defaultReasoning = reasoning?.Choices.FirstOrDefault(choice => choice.IsDefault) is { } defaultChoice
            ? ParseReasoningEffort(defaultChoice.Id)
            : null;
        var serviceTiers = model.Options.FirstOrDefault(option =>
                string.Equals(option.Id, "service-tier", StringComparison.OrdinalIgnoreCase))?.Choices
            .Select(choice => new CodexServiceTierOption(
                choice.Id,
                choice.DisplayName,
                choice.Description))
            .ToArray() ?? [];
        return new CodexModelOption(
            model.Id,
            model.Id,
            model.DisplayName,
            model.Description,
            model.IsDefault,
            model.IsHidden,
            defaultReasoning,
            reasoningOptions,
            serviceTiers,
            model.AvailabilityMessage,
            InputModalities: model.InputModalities.Select(modality => modality switch
            {
                HarnessInputModality.Image => CodexInputModality.Image,
                _ => CodexInputModality.Text
            }).Distinct().ToArray());
    }

    private static CodexReasoningEffort? ParseReasoningEffort(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "none" => CodexReasoningEffort.None,
            "minimal" => CodexReasoningEffort.Minimal,
            "low" => CodexReasoningEffort.Low,
            "medium" => CodexReasoningEffort.Medium,
            "high" => CodexReasoningEffort.High,
            "xhigh" => CodexReasoningEffort.XHigh,
            _ => null
        };

    private HarnessId ResolveHarnessId(ProjectThreadState? thread = null) => new(
        string.IsNullOrWhiteSpace(thread?.HarnessId)
            ? string.IsNullOrWhiteSpace(settings.DefaultHarnessId)
                ? KnownHarnessIds.Codex
                : settings.DefaultHarnessId
            : thread.HarnessId);

    private ProjectThreadState? FindThread(string? threadId) => string.IsNullOrWhiteSpace(threadId)
        ? null
        : settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal)) is { } persisted
                ? SettingsStorageMapper.ToPresentation(persisted)
                : null;

    private HarnessCapabilities ResolveCapabilities(ProjectThreadState? thread = null)
    {
        var harnessId = ResolveHarnessId(thread);
        if (harnessRuntimeCoordinator.TryGetSession(harnessId, out var session))
        {
            return session!.Capabilities;
        }
        return harnessRuntimeCoordinator.Registry.TryGet(harnessId, out var harness)
            ? harness!.Descriptor.Capabilities
            : HarnessCapabilities.None;
    }

    private string ResolveHarnessName(ProjectThreadState? thread = null)
    {
        var harnessId = ResolveHarnessId(thread);
        return harnessRuntimeCoordinator.Registry.TryGet(harnessId, out var harness)
            ? harness!.Descriptor.DisplayName
            : harnessId.Value;
    }

    private bool Supports(HarnessCapability capability, ProjectThreadState? thread = null) =>
        ResolveCapabilities(thread).Supports(capability);

    private bool IsHarnessReady(ProjectThreadState? thread = null)
    {
        var harnessId = ResolveHarnessId(thread);
        if (!harnessRuntimeCoordinator.Registry.TryGet(harnessId, out _))
        {
            return false;
        }
        return harnessId != HarnessId.Codex ||
            currentCodex.IsFound &&
            currentAuth.Readiness is not (AuthReadiness.Unavailable or AuthReadiness.NotSignedIn);
    }

    private bool IsHarnessConnected(ProjectThreadState? thread = null)
    {
        var harnessId = ResolveHarnessId(thread);
        return harnessRuntimeCoordinator.TryGetSession(harnessId, out var session) &&
            session?.State == HarnessSessionState.Connected;
    }

    private bool CanSubmitTurn() =>
        !IsShuttingDown &&
        IsHarnessReady(SelectedThread) &&
        Supports(HarnessCapability.StartTurn, SelectedThread) &&
        (SelectedThread is not null || Supports(HarnessCapability.CreateConversation)) &&
        TaskWorkspace.CanSubmitAttachments;

    private bool CanStartCodeReview() =>
        !IsShuttingDown &&
        !IsTurnRunning &&
        !string.IsNullOrWhiteSpace(SelectedProjectPath) &&
        IsHarnessReady(SelectedThread) &&
        (SelectedThread is not null || Supports(HarnessCapability.CreateConversation)) &&
        (SelectedThread is null ||
         SelectedThread is { IsArchived: false } && ResolveHarnessId(SelectedThread) == HarnessId.Codex);

    private bool CanCancelTurn()
    {
        return !IsShuttingDown &&
            IsTurnRunning &&
            IsHarnessConnected(FindThread(activeThreadId)) &&
            Supports(HarnessCapability.CancelTurn, FindThread(activeThreadId)) &&
            !string.IsNullOrWhiteSpace(activeThreadId) &&
            !string.IsNullOrWhiteSpace(activeTurnId);
    }

    private bool CanManageThreads() =>
        !IsShuttingDown &&
        IsHarnessReady() &&
        Supports(HarnessCapability.CreateConversation);

    private bool CanCreateThreadInCurrentScope() =>
        CanManageThreads() &&
        (!string.IsNullOrWhiteSpace(SelectedProjectPath) || !string.IsNullOrWhiteSpace(generalWorkspacePath));

    private bool CanCreateGeneralThread() =>
        CanManageThreads() && !string.IsNullOrWhiteSpace(generalWorkspacePath);

    private bool CanUseSelectedThread() =>
        !IsShuttingDown &&
        SelectedThread is not null &&
        IsHarnessReady(SelectedThread) &&
        Supports(HarnessCapability.ResumeConversation, SelectedThread);

    private bool CanForkSelectedThread() =>
        !IsShuttingDown &&
        SelectedThread is not null &&
        IsHarnessReady(SelectedThread) &&
        Supports(HarnessCapability.ForkConversation, SelectedThread);

    private bool CanArchiveSelectedThread() =>
        CanUseSelectedThread() &&
        Supports(HarnessCapability.ArchiveConversation, SelectedThread) &&
        SelectedThread?.IsArchived == false &&
        !IsTurnRunning &&
        !SelectedThreadHasQueuedFollowUps();

    private bool CanUnarchiveSelectedThread() =>
        SelectedThread is not null &&
        IsHarnessReady(SelectedThread) &&
        Supports(HarnessCapability.ArchiveConversation, SelectedThread) &&
        SelectedThread.IsArchived;

    private bool CanToggleSelectedThreadPin() =>
        !IsShuttingDown && SelectedThread is not null;

    private bool CanRenameSelectedThread() =>
        !IsShuttingDown &&
        SelectedThread is not null &&
        IsHarnessReady(SelectedThread) &&
        Supports(HarnessCapability.RenameConversation, SelectedThread);

    private bool CanDeleteSelectedThread() =>
        !IsShuttingDown &&
        SelectedThread is { IsRunning: false } &&
        (SelectedThread.IsArchived || Supports(HarnessCapability.ArchiveConversation, SelectedThread)) &&
        !IsTurnRunning &&
        !SelectedThreadHasQueuedFollowUps();

    private bool CanRemoveSelectedWorktree() =>
        !IsShuttingDown &&
        SelectedThread is not null &&
        SelectedThread?.ScopeKind == ThreadScopeKind.Project &&
        SelectedThread?.IsRunning == false &&
        string.Equals(SelectedThread.Mode, "worktree", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(SelectedThread.WorkspacePath) &&
        !SelectedThreadHasQueuedFollowUps();

    private bool SelectedThreadHasQueuedFollowUps()
    {
        var threadId = SelectedThread?.ThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        return followUpQueue.HasQueue(threadId)
            ? followUpQueue.GetCount(threadId) > 0
            : SelectedThread?.QueuedFollowUps.Count > 0;
    }

    private bool CanSteerTurn() =>
        !IsShuttingDown &&
        IsTurnRunning &&
        IsHarnessConnected(FindThread(activeThreadId)) &&
        Supports(HarnessCapability.SteerTurn, FindThread(activeThreadId)) &&
        !string.IsNullOrWhiteSpace(activeThreadId) &&
        !string.IsNullOrWhiteSpace(activeTurnId) &&
        (!string.IsNullOrWhiteSpace(SteeringText) || TaskWorkspace.HasAttachments || Git.HasReviewComments) &&
        TaskWorkspace.CanSubmitAttachments;

    private bool CanLoadModels() =>
        !IsShuttingDown &&
        IsHarnessReady(SelectedThread) &&
        Supports(HarnessCapability.ModelCatalog, SelectedThread);

    private async Task<CodexThreadReadResult> ReadAgentThreadAsync(string threadId)
    {
        if (IsShuttingDown)
        {
            throw new InvalidOperationException("SynthiaCode is shutting down.");
        }

        var result = await appServerSessionCoordinator.ReadThreadAsync(
            new CodexThreadReadRequest(threadId),
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
        StatusMessage = $"Opened agent {threadId}";
        return result;
    }

    private async Task SteerAgentAsync(string threadId, string turnId, string message)
    {
        if (IsShuttingDown)
        {
            throw new InvalidOperationException("SynthiaCode is shutting down.");
        }

        await appServerSessionCoordinator.SteerTurnAsync(
            new CodexTurnSteerRequest(threadId, turnId, message),
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
        StatusMessage = $"Steered agent {threadId}";
    }

    private async Task StopAgentAsync(string threadId, string turnId)
    {
        if (IsShuttingDown)
        {
            throw new InvalidOperationException("SynthiaCode is shutting down.");
        }

        await appServerSessionCoordinator.CancelTurnAsync(
            threadId,
            turnId,
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
        StatusMessage = $"Stopped agent {threadId}";
    }

    private void RaiseThreadCommandStates()
    {
        ProjectWorkspace.RaiseCommandStates();
        TaskWorkspace.RaiseCommandStates();
    }

    private string GetActiveWorkspacePath()
    {
        var path = SelectedThread?.WorkspacePath ?? SelectedProjectPath ?? generalWorkspacePath
            ?? throw new InvalidOperationException(generalWorkspaceError ?? "The General workspace is unavailable.");
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"The active workspace is unavailable: {path}");
        }

        return path;
    }

    private IReadOnlyList<string> GetActiveWorkspaceRoots()
    {
        var workspacePath = GetActiveWorkspacePath();
        var projectPath = SelectedThread?.ScopeKind == ThreadScopeKind.Project
            ? SelectedThread.ProjectPath
            : SelectedProjectPath;
        return GetWorkspaceRoots(workspacePath, projectPath);
    }

    private IReadOnlyList<string> GetWorkspaceRootsForThread(string threadId, string workspacePath)
    {
        var thread = settings.ProjectThreads.FirstOrDefault(item =>
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));
        return GetWorkspaceRoots(workspacePath, thread?.ScopeKind == ThreadScopeKind.Project ? thread.ProjectPath : null);
    }

    private IReadOnlyList<string> GetQueuedWorkspaceRoots(
        string threadId,
        QueuedTurnOptionsSnapshot options) =>
        options.WorkspaceRoots is { Count: > 0 }
            ? ProjectFolderSet.NormalizePersisted(options.WorkspacePath, options.WorkspaceRoots)
            : GetWorkspaceRootsForThread(threadId, options.WorkspacePath);

    private IReadOnlyList<string> GetWorkspaceRoots(string workspacePath, string? projectPath)
    {
        var workspace = Path.GetFullPath(workspacePath);
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return [workspace];
        }

        var project = settings.RecentProjects.FirstOrDefault(item =>
            ProjectFolderSet.PathsEqual(item.Path, projectPath));
        var attached = (project?.FolderPaths ?? [Path.GetFullPath(projectPath)])
            .Where(Directory.Exists)
            .ToList();
        return ProjectFolderSet.NormalizePersisted(workspace, attached);
    }

    private CodexResolvedPermissionMode ResolvePermissionPolicy()
    {
        var resolved = ExecutionPolicy.ResolvedPolicy;
        if (!resolved.IsAvailable)
        {
            throw new InvalidOperationException(
                resolved.UnavailableReason ?? "The selected permission mode is unavailable.");
        }

        return resolved;
    }

    private ThreadScopeKey GetCurrentScope() =>
        SelectedThread?.ScopeKey
        ?? (!string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? ThreadScopeKey.ForProject(SelectedProjectPath)
            : ThreadScopeKey.General);

    private string GetWorkspacePath(ThreadScopeKey scope)
    {
        var path = scope.Kind == ThreadScopeKind.General
            ? generalWorkspacePath
            : scope.ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(scope.Kind == ThreadScopeKind.General
                ? generalWorkspaceError ?? "The General workspace is unavailable."
                : "The selected project workspace is unavailable.");
        }

        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"The active workspace is unavailable: {path}");
        }

        return path;
    }

    private StartConversationCommand CreateConversationStartCommand(
        string cwd,
        CodexInstructionSnapshot instructionSnapshot,
        string? projectPath) =>
        attachmentDraftService.CreateHarnessConversationStart(
            ConversationId.New(),
            ResolvePermissionPolicy(),
            ModelOverride,
            cwd,
            instructionSnapshot.DeveloperInstructions,
            instructionSnapshot.BaseInstructions,
            GetWorkspaceRoots(cwd, projectPath));

    private ThreadResumeUseCaseRequest CreateThreadResumeRequest(string threadId, string cwd) => new(
        threadId,
        CreateHarnessConnectionOptions(cwd),
        attachmentDraftService.CreateHarnessConversationResume(
            GetConversationAddress(threadId),
            ResolvePermissionPolicy(),
            ModelOverride,
            cwd,
            ResolveInstructionSnapshot(threadId).DeveloperInstructions,
            ResolveInstructionSnapshot(threadId).BaseInstructions,
            GetWorkspaceRootsForThread(threadId, cwd)));

    private ForkConversationCommand CreateThreadForkRequest(
        ProjectThreadState thread, string cwd, CodexInstructionSnapshot instructionSnapshot) =>
        attachmentDraftService.CreateHarnessConversationFork(
            ConversationId.New(),
            GetConversationAddress(thread.ThreadId),
            ResolvePermissionPolicy(),
            ModelOverride,
            cwd,
            instructionSnapshot.DeveloperInstructions,
            instructionSnapshot.BaseInstructions,
            GetWorkspaceRoots(cwd, thread.ScopeKind == ThreadScopeKind.Project ? thread.ProjectPath : null));

    private CodexInstructionSnapshot ResolveDefaultInstructionSnapshot() => new(
        settings.CustomDeveloperInstructionsEnabled
            ? NormalizeInstructionOverride(settings.CustomDeveloperInstructions)
            : null,
        settings.CustomBaseInstructionsEnabled
            ? NormalizeInstructionOverride(settings.CustomBaseInstructions)
            : null);

    private CodexInstructionSnapshot ResolveInstructionSnapshot(string threadId)
    {
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        return persisted is null
            ? default
            : new CodexInstructionSnapshot(
                persisted.AppliedDeveloperInstructions,
                persisted.AppliedBaseInstructions);
    }

    private static string? NormalizeInstructionOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private StartTurnCommand CreateHarnessTurnStartCommand(
        string threadId,
        string prompt,
        IReadOnlyList<AttachmentReference> attachments,
        string cwd)
    => attachmentDraftService.CreateHarnessTurnStart(new HarnessTurnRequestComposition(
        GetConversationAddress(threadId), prompt, attachments, cwd, ResolvePermissionPolicy(), ModelOverride, ReasoningEffortOverride,
        TaskWorkspace.ServiceTierSelection, TaskWorkspace.SelectedModel,
        TaskWorkspace.SkillSelector.ResolveSkillInputs(prompt),
        GetWorkspaceRootsForThread(threadId, cwd)));

    private ConversationAddress GetConversationAddress(string threadId)
    {
        var state = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (state is not null)
        {
            return state.GetConversationAddress();
        }

        var harnessId = ResolveHarnessId();
        return new ConversationAddress(
            new ConversationId(AppSettingsHarnessMigration.CreateDeterministicConversationId(
                harnessId.Value,
                threadId)),
            harnessId,
            threadId);
    }

    private HarnessConnectionOptions CreateHarnessConnectionOptions(string? workspacePath)
    {
        IReadOnlyDictionary<string, string>? harnessSettings = string.IsNullOrWhiteSpace(settings.PreferredCodexPath)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executablePath"] = settings.PreferredCodexPath
            };
        return new HarnessConnectionOptions(workspacePath, harnessSettings);
    }

    private QueuedTurnOptionsSnapshot CaptureQueuedTurnOptions(string threadId, string workspacePath) =>
        attachmentDraftService.CaptureQueuedOptions(
            ResolvePermissionPolicy(), ExecutionPolicy.PermissionMode, workspacePath,
            ModelOverride, ReasoningEffortOverride, TaskWorkspace.ServiceTierSelection,
            GetWorkspaceRootsForThread(threadId, workspacePath));

    private string GetWorkspacePathForThread(string threadId)
    {
        var state = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thread '{threadId}' is no longer available.");
        var path = Path.GetFullPath(state.WorkspacePath ?? state.ProjectPath);
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"The queued follow-up workspace is unavailable: {path}");
        }

        return path;
    }

    private async Task TryDrainFollowUpQueueAsync(string threadId)
    {
        if (IsShuttingDown ||
            conversationWorkflow.IsRunning(threadId) ||
            !followUpQueue.HasQueue(threadId))
        {
            return;
        }

        if (followUpQueue.GetFirstPending(threadId) is null)
        {
            return;
        }
        await StartQueuedFollowUpAsync(threadId).ConfigureAwait(true);
    }

    private async Task StartQueuedFollowUpAsync(string threadId)
    {
        async Task<PreparedHarnessTurn> PrepareStartRequestAsync(
            QueuedFollowUpSnapshot queued,
            CancellationToken cancellationToken)
        {
            var options = queued.Options;
            var workspacePath = Path.GetFullPath(options.WorkspacePath);
            var models = await appServerSessionCoordinator
                .ListModelsAsync(cancellationToken)
                .ConfigureAwait(true);
            var requirements = await appServerSessionCoordinator
                .ReadExecutionPolicyRequirementsAsync(cancellationToken)
                .ConfigureAwait(true);
            var effectiveConfig = await appServerSessionCoordinator
                .ReadExecutionPolicyConfigAsync(workspacePath, cancellationToken)
                .ConfigureAwait(true);
            var profiles = await appServerSessionCoordinator
                .ListPermissionProfilesAsync(workspacePath, cancellationToken)
                .ConfigureAwait(true);
            var resolved = QueuedTurnOptionResolver.Resolve(
                options,
                models,
                effectiveConfig,
                requirements,
                profiles);
            var effectivePrompt = GitInlineCommentPromptFormatter.AppendToPrompt(
                queued.Text,
                queued.ReviewComments);
            var command = attachmentDraftService.CreateHarnessTurnStart(new HarnessTurnRequestComposition(
                GetConversationAddress(threadId),
                effectivePrompt,
                queued.Attachments,
                workspacePath,
                resolved.Permissions,
                string.IsNullOrWhiteSpace(options.Model) ? null : resolved.Model.Model,
                options.ReasoningEffort?.ToProtocolValue(),
                options.ServiceTier,
                resolved.Model,
                queued.SkillInputs,
                GetQueuedWorkspaceRoots(threadId, options)));
            return new PreparedHarnessTurn(
                CreateHarnessConnectionOptions(workspacePath),
                command,
                effectivePrompt);
        }

        var result = await followUpQueue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            settings,
            threadId,
            PrepareStartRequestAsync)).ConfigureAwait(true);
        var dispatch = result.Dispatch;
        if (!dispatch.Attempted) return;
        ApplyFollowUpQueueSnapshot(threadId, result.Snapshot);
        TaskWorkspace.NotifyQueuedFollowUpsChanged();
        if (!string.IsNullOrWhiteSpace(dispatch.ErrorMessage))
        {
            StatusMessage = dispatch.ErrorMessage;
            logger.Log(
                AppLogLevel.Error,
                "queued_follow_up_start_failed",
                "A queued follow-up could not be started and requires attention.",
                properties: new Dictionary<string, string?> { ["error"] = dispatch.ErrorMessage });
            return;
        }
        if (string.IsNullOrWhiteSpace(dispatch.TurnId) || dispatch.TurnStatus is null) return;
        var turnStatus = dispatch.TurnStatus.Value;
        UpdateThreadActivity(
            threadId,
            turnStatus == CodexTurnStatus.Running,
            turnStatus == CodexTurnStatus.Running ? "Running" : turnStatus.ToString());
        if (string.Equals(threadId, activeThreadId, StringComparison.Ordinal))
        {
            activeTurnId = turnStatus == CodexTurnStatus.Running ? dispatch.TurnId : null;
            IsTurnRunning = turnStatus == CodexTurnStatus.Running;
            StatusMessage = IsTurnRunning
                ? "Queued follow-up running"
                : $"Codex turn {turnStatus.ToString().ToLowerInvariant()}";
        }
        if (!conversationWorkflow.IsRunning(threadId))
        {
            _ = TryDrainFollowUpQueueAsync(threadId);
        }
        RaiseThreadCommandStates();
    }

    private async Task PersistFollowUpQueueAsync(string threadId)
    {
        var mutation = await followUpQueue.PersistAsync(settings, threadId).ConfigureAwait(true);
        ApplyFollowUpQueueMutation(threadId, mutation);
    }

    private void ApplyFollowUpQueueMutation(string threadId, FollowUpQueueMutationResult mutation)
    {
        if (!mutation.Found) return;
        ApplyFollowUpQueueSnapshot(threadId, mutation.Snapshot, mutation.UpdatedAt);
    }

    private void ApplyFollowUpQueueSnapshot(
        string threadId,
        ConversationWorkspaceSnapshot snapshot,
        DateTimeOffset? updatedAt = null)
    {
        if (conversationWorkflow.IsRunning(threadId))
        {
            UpdateThreadActivity(threadId, isRunning: true, "Running");
        }
        var presentation = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (presentation is not null)
        {
            presentation.QueuedFollowUps = snapshot.QueuedFollowUps.Select(item => item.Clone()).ToList();
            presentation.UpdatedAt = updatedAt
                ?? settings.ProjectThreads.FirstOrDefault(thread =>
                    string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))?.UpdatedAt
                ?? presentation.UpdatedAt;
        }
        if (string.Equals(threadId, activeThreadId, StringComparison.Ordinal))
        {
            TaskWorkspace.ApplyConversationSnapshot(snapshot);
        }
    }

    private async Task SaveThreadStateAndMaybeDrainAsync(
        string threadId,
        bool shouldDrain)
    {
        await SaveThreadStateAsync(threadId).ConfigureAwait(true);
        if (shouldDrain)
        {
            await TryDrainFollowUpQueueAsync(threadId).ConfigureAwait(true);
        }
    }

    private async Task EnsureAppServerSessionAsync(CancellationToken cancellationToken = default)
    {
        await appServerSessionCoordinator.EnsureConnectedAsync(currentCodex, cancellationToken).ConfigureAwait(true);
    }

    private async Task EnsureHarnessSessionAsync(
        HarnessId harnessId,
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (harnessId == HarnessId.Codex)
        {
            await EnsureAppServerSessionAsync(cancellationToken).ConfigureAwait(true);
        }

        await harnessRuntimeCoordinator.GetOrConnectAsync(
            harnessId,
            CreateHarnessConnectionOptions(workspacePath),
            cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadSelectedGoalAsync(string threadId, CancellationToken cancellationToken)
    {
        if (!string.Equals(SelectedThread?.ThreadId, threadId, StringComparison.Ordinal))
        {
            return;
        }

        TaskWorkspace.SetGoalLoading();
        try
        {
            var workspacePath = SelectedThread?.WorkspacePath ?? SelectedThread?.ProjectPath;
            await EnsureHarnessSessionAsync(HarnessId.Codex, workspacePath, cancellationToken).ConfigureAwait(true);
            var loaded = await appServerSessionCoordinator
                .GetThreadGoalAsync(threadId, cancellationToken)
                .ConfigureAwait(true);
            if (string.Equals(SelectedThread?.ThreadId, threadId, StringComparison.Ordinal))
            {
                TaskWorkspace.ApplyGoal(loaded);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (CodexAppServerProtocolException exception) when (exception.Code == -32601)
        {
            if (string.Equals(SelectedThread?.ThreadId, threadId, StringComparison.Ordinal))
            {
                TaskWorkspace.SetGoalUnsupported("Goal mode requires a newer Codex runtime.");
            }
        }
        catch (Exception exception)
        {
            logger.Log(
                AppLogLevel.Warning,
                "goal_load_failed",
                "The selected Codex goal could not be loaded.",
                exception: exception);
            if (string.Equals(SelectedThread?.ThreadId, threadId, StringComparison.Ordinal))
            {
                TaskWorkspace.SetGoalLoadError($"Could not load the goal: {exception.Message}");
            }
        }
    }

    private async Task<CodexThreadGoal> SetSelectedGoalAsync(string objective)
    {
        var thread = GetSelectedGoalThread();
        await EnsureHarnessSessionAsync(
            HarnessId.Codex,
            thread.WorkspacePath ?? thread.ProjectPath,
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
        return await appServerSessionCoordinator.SetThreadGoalAsync(
            new CodexThreadGoalSetRequest(
                thread.ThreadId,
                objective,
                CodexThreadGoalStatus.Active),
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
    }

    private async Task<CodexThreadGoal> SetSelectedGoalStatusAsync(CodexThreadGoalStatus status)
    {
        var thread = GetSelectedGoalThread();
        await EnsureHarnessSessionAsync(
            HarnessId.Codex,
            thread.WorkspacePath ?? thread.ProjectPath,
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
        return await appServerSessionCoordinator.SetThreadGoalAsync(
            new CodexThreadGoalSetRequest(thread.ThreadId, Status: status),
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
    }

    private async Task<bool> ClearSelectedGoalAsync()
    {
        var thread = GetSelectedGoalThread();
        await EnsureHarnessSessionAsync(
            HarnessId.Codex,
            thread.WorkspacePath ?? thread.ProjectPath,
            appServerWarmUpCancellation.Token).ConfigureAwait(true);
        return await appServerSessionCoordinator
            .ClearThreadGoalAsync(thread.ThreadId, appServerWarmUpCancellation.Token)
            .ConfigureAwait(true);
    }

    private async Task StartSelectedGoalWorkAsync(string threadId, string objective)
    {
        _ = GetSelectedGoalThread();
        if (IsTurnRunning || !string.Equals(SelectedThread?.ThreadId, threadId, StringComparison.Ordinal))
        {
            return;
        }

        TaskWorkspace.Prompt = objective;
        await SubmitPromptAsync().ConfigureAwait(true);
    }

    private ProjectThreadState GetSelectedGoalThread()
    {
        if (!CanManageSelectedGoal() || SelectedThread is null)
        {
            throw new InvalidOperationException("Select an active Codex chat to manage its goal.");
        }

        return SelectedThread;
    }

    private bool CanManageSelectedGoal() =>
        enableGoalMode &&
        !IsShuttingDown &&
        SelectedThread is { IsArchived: false } &&
        ResolveHarnessId(SelectedThread) == HarnessId.Codex &&
        IsHarnessReady(SelectedThread);

    private async Task RefreshExecutionPolicyAsync(string? cwd, CancellationToken cancellationToken)
    {
        try
        {
            var requirements = await appServerSessionCoordinator
                .ReadExecutionPolicyRequirementsAsync(cancellationToken)
                .ConfigureAwait(true);
            var config = await appServerSessionCoordinator
                .ReadExecutionPolicyConfigAsync(cwd, cancellationToken)
                .ConfigureAwait(true);
            var profileResult = string.IsNullOrWhiteSpace(cwd)
                ? new CodexPermissionProfileListResult([], null, IsSupported: false)
                : await appServerSessionCoordinator
                    .ListPermissionProfilesAsync(cwd, cancellationToken)
                    .ConfigureAwait(true);
            ExecutionPolicy.ApplyRequirements(requirements);
            ExecutionPolicy.ApplyEffectiveConfig(config);
            ExecutionPolicy.ApplyCapabilities(new CodexPermissionCapabilities(
                profileResult.IsSupported,
                SupportsAutoReview: true));
            ExecutionPolicy.ApplyProfiles(profileResult.Profiles);
            executionPolicyLoaded = true;
            executionPolicyCwd = cwd;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            executionPolicyLoaded = true;
            executionPolicyCwd = cwd;
            logger.Log(
                AppLogLevel.Warning,
                "execution_policy_read_failed",
                "Codex execution-policy configuration could not be read; saved overrides remain active.",
                exception: ex);
        }
    }

    private void OnExecutionPolicyChanged()
    {
        settings.PermissionMode = ExecutionPolicy.PermissionModeSettingsValue;
        settings.CustomPermissionProfileId = ExecutionPolicy.CustomProfileSettingsValue;
        settings.ExecutionPolicySchemaVersion = AppSettingsPermissionMigration.CurrentSchemaVersion;
        settings.SandboxModeOverride = ExecutionPolicy.SandboxSettingsValue;
        settings.ApprovalPolicyOverride = ExecutionPolicy.ApprovalSettingsValue;
        executionPolicyLoaded = false;
        _ = SaveExecutionPolicySettingsAsync();
    }

    private async Task SaveExecutionPolicySettingsAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "execution_policy_save_failed",
                "Execution-policy settings could not be saved.",
                exception: ex);
        }
    }

    private void OnAppServerConnectionFailed(object? sender, AppServerConnectionFailedEventArgs args)
    {
        void ApplyFailure()
        {
            foreach (var threadId in conversationWorkflow.SnapshotRunningThreadIds())
            {
                UpdateThreadActivity(threadId, isRunning: false, "Recovery needed");
            }

            conversationWorkflow.ClearRuntimeState();
            executionPolicyLoaded = false;
            activeTurnId = null;
            IsTurnRunning = false;
            Account.MarkDisconnected();
            ApprovalQueue.Clear();
            AppServerHealth = "Codex reconnecting";
            StatusMessage = $"Codex app-server stopped: {args.Exception.Message}. The next action will restart it.";
        }

        if (synchronizationContext is null)
        {
            ApplyFailure();
        }
        else
        {
            synchronizationContext.Post(_ => ApplyFailure(), null);
        }
    }

    private void OnAppServerNotificationReceived(object? sender, CodexAppServerNotification notification)
    {
        DispatchAppServerNotification(notification);
    }

    private void OnHarnessEventReceived(object? sender, HarnessEvent harnessEvent)
    {
        void ApplyEvent() => ApplyConversationEvent(
            conversationWorkflow.ApplyHarnessEvent(harnessEvent));

        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplyEvent();
            return;
        }

        synchronizationContext.Post(_ => ApplyEvent(), null);
    }

    private void OnServerRequestReceived(object? sender, CodexServerRequest request)
    {
        void ApplyRequest()
        {
            ApprovalQueue.Enqueue(request);
            StatusMessage = $"Approval required: {ApprovalQueue.ActivePrompt?.Kind}";
        }

        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplyRequest();
        }
        else
        {
            synchronizationContext.Post(_ => ApplyRequest(), null);
        }
    }

    private void InvokeOnCapturedSynchronizationContext(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var context = synchronizationContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            action();
            return;
        }

        context.Send(_ => action(), null);
    }

    private void OnAppServerStateChanged(object? sender, AppServerSessionStateChangedEventArgs args)
    {
        void ApplyState()
        {
            AppServerHealth = args.State switch
            {
                AppServerSessionState.Connecting => "Codex connecting",
                AppServerSessionState.Connected => "Codex connected",
                AppServerSessionState.Reconnecting => "Codex reconnecting",
                AppServerSessionState.Unavailable => "Codex unavailable",
                AppServerSessionState.Disposed => "Codex stopped",
                _ => "Codex idle"
            };
            if (args.State == AppServerSessionState.Connected && Account.IsActive && Account.IsStale)
            {
                _ = Account.RefreshAsync(appServerWarmUpCancellation.Token);
            }
            if (args.State == AppServerSessionState.Connected &&
                args.PreviousState is AppServerSessionState.Reconnecting or AppServerSessionState.Unavailable)
            {
                TaskWorkspace.InvalidateModelCatalog();
                EffectiveCodexSettings.NotifyContextChanged();
                if (Skills.IsActive)
                {
                    _ = EffectiveCodexSettings.RefreshIfStaleAsync(appServerWarmUpCancellation.Token);
                }
                if (enableGoalMode &&
                    SelectedThread is { IsArchived: false } selected &&
                    ResolveHarnessId(selected) == HarnessId.Codex)
                {
                    _ = LoadSelectedGoalAsync(selected.ThreadId, appServerWarmUpCancellation.Token);
                }
            }
        }

        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplyState();
        }
        else
        {
            synchronizationContext.Post(_ => ApplyState(), null);
        }
    }

    private void DispatchAppServerNotification(CodexAppServerNotification notification)
    {
        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplyNotification(notification);
            return;
        }

        synchronizationContext.Post(_ => ApplyNotification(notification), null);
    }

    private void ApplyNotification(CodexAppServerNotification notification)
    {
        if (notification.Kind == CodexAppServerNotificationKind.SkillsChanged)
        {
            return;
        }

        if (notification.Kind is CodexAppServerNotificationKind.ThreadGoalUpdated or
            CodexAppServerNotificationKind.ThreadGoalCleared)
        {
            if (!enableGoalMode)
            {
                return;
            }

            if (!string.Equals(notification.ThreadId, SelectedThread?.ThreadId, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                CodexThreadGoal? updatedGoal = null;
                if (notification.Kind == CodexAppServerNotificationKind.ThreadGoalUpdated)
                {
                    updatedGoal = notification.Params["goal"] is JsonObject goalValue
                        ? CodexThreadGoalJson.Parse(goalValue)
                        : throw new InvalidDataException("The goal update did not include a goal.");
                    if (!string.Equals(updatedGoal.ThreadId, notification.ThreadId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The goal update belongs to a different thread.");
                    }
                }

                TaskWorkspace.ApplyGoal(updatedGoal);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                logger.Log(
                    AppLogLevel.Warning,
                    "goal_notification_invalid",
                    "Codex sent an invalid goal notification.",
                    exception: exception);
                TaskWorkspace.SetGoalLoadError("Codex sent an invalid goal update. Refresh the chat to retry.");
            }
            return;
        }

        if (notification.Kind == CodexAppServerNotificationKind.ServerRequestResolved && notification.RequestId is { } requestId)
        {
            if (ApprovalQueue.Resolve(requestId))
            {
                StatusMessage = ApprovalQueue.HasPendingApproval
                    ? $"Approval required: {ApprovalQueue.ActivePrompt?.Kind}"
                    : "Approval request resolved";
            }
            return;
        }

        if (IsCodeReviewLifecycleNotification(notification))
        {
            ApplyConversationEvent(conversationWorkflow.ApplyThreadNotification(notification));
            return;
        }

        if (Account.TryApplyNotification(notification))
        {
            if (notification.Kind is CodexAppServerNotificationKind.AccountUpdated or CodexAppServerNotificationKind.AccountLoginCompleted)
            {
                TaskWorkspace.InvalidateModelCatalog();
            }
            return;
        }
    }

    private static bool IsCodeReviewLifecycleNotification(CodexAppServerNotification notification) =>
        notification.Kind is CodexAppServerNotificationKind.ItemStarted or CodexAppServerNotificationKind.ItemCompleted &&
        notification.Params["item"] is JsonObject item &&
        item["type"] is JsonValue typeValue &&
        typeValue.TryGetValue<string>(out var type) &&
        type is "enteredReviewMode" or "exitedReviewMode";

    private void ApplyConversationEvent(ConversationNotificationResult routed)
    {
        var routedThreadId = routed.ThreadId;
        var routedSnapshot = routed.Snapshot;

        if (string.Equals(routedThreadId, activeThreadId, StringComparison.Ordinal))
        {
            TaskWorkspace.ApplyConversationSnapshot(routedSnapshot);
            activeTurnId = routedSnapshot.ActiveTurnId ?? activeTurnId;
        }

        if (routed.IsTurnCompleted)
        {
            if (!string.IsNullOrWhiteSpace(routedThreadId))
            {
                UpdateThreadActivity(
                    routedThreadId,
                    isRunning: false,
                    routedSnapshot.ActiveTurnStatus.ToString());
            }

            if (string.Equals(routedThreadId, activeThreadId, StringComparison.Ordinal))
            {
                IsTurnRunning = false;
                activeTurnId = null;
            }

            if (routedSnapshot.ActiveTurnStatus == CodexTurnStatus.Completed)
            {
                if (string.Equals(routedThreadId, activeThreadId, StringComparison.Ordinal))
                {
                    PromptText = string.Empty;
                    StatusMessage = "Codex turn completed";
                }
            }
            else if (routedSnapshot.RequiresAuthentication)
            {
                StatusMessage = "Codex authentication failed. Sign in and retry.";
            }
            else
            {
                StatusMessage = $"Codex turn {routedSnapshot.ActiveTurnStatus.ToString().ToLowerInvariant()}";
            }

            if (!string.IsNullOrWhiteSpace(routedThreadId))
            {
                _ = SaveThreadStateAndMaybeDrainAsync(
                    routedThreadId,
                    routedSnapshot.ActiveTurnStatus == CodexTurnStatus.Completed);
            }
            _ = Git.RefreshAsync();
        }

        if (!string.IsNullOrWhiteSpace(routedThreadId) &&
            routed.IsArchived is not null &&
            settings.ProjectThreads.Any(thread => string.Equals(thread.ThreadId, routedThreadId, StringComparison.Ordinal)))
        {
            conversationWorkflow.SetThreadArchived(
                settings,
                routedThreadId,
                archived: routed.IsArchived.Value);
        }

        TaskWorkspace.NotifyResponseChanged();
        OnPropertyChanged(nameof(FinalResponse));
        RaiseThreadCommandStates();
    }

    private void UpdateThreadActivity(string threadId, bool isRunning, string status)
    {
        var state = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (state is null)
        {
            return;
        }

        state.IsRunning = isRunning;
        state.TurnStatus = status;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        var presentation = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (presentation is not null)
        {
            presentation.IsRunning = isRunning;
            presentation.TurnStatus = status;
            presentation.UpdatedAt = state.UpdatedAt;
        }
    }

    private async Task SaveThreadStateAsync(string threadId)
    {
        try
        {
            var result = await threadStatePersistence.SaveAsync(settings, threadId).ConfigureAwait(true);
            var presentation = ProjectThreads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
            if (result is not null && presentation is not null && !ReferenceEquals(presentation, result.State))
            {
                presentation.FinalResponse = result.State.FinalResponse;
                presentation.TimelineItems = [.. result.State.TimelineItems];
                presentation.RawEvents = [.. result.State.RawEvents];
                presentation.ConversationTurns = result.State.ConversationTurns.Select(CloneConversationTurn).ToList();
                presentation.ContextTokensUsed = result.State.ContextTokensUsed;
                presentation.ContextWindowTokens = result.State.ContextWindowTokens;
                presentation.ContextCompactionCount = result.State.ContextCompactionCount;
                presentation.UpdatedAt = result.UpdatedAt;
            }
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "thread_state_save_failed", "Could not persist thread state.", exception: ex);
        }
    }

    private void RestorePersistedThreadState()
    {
        ProjectThreads.Clear();
        var scope = string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? ThreadScopeKey.General
            : ThreadScopeKey.ForProject(SelectedProjectPath);
        foreach (var persisted in conversationWorkflow.GetThreads(settings, scope))
        {
            ProjectThreads.Add(persisted);
            conversationWorkflow.RestoreThread(persisted);
        }

        SelectedThread = conversationWorkflow.GetActiveThread(settings, scope);
        if (SelectedThread is null)
        {
            TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.ResetActiveConversation());
            OnPropertyChanged(nameof(TimelineItems));
            OnPropertyChanged(nameof(RawEvents));
            OnPropertyChanged(nameof(FinalResponse));
        }

        RefreshProjectNavigation();
    }

    private ProjectThreadState? FindProjectThreadState()
    {
        return SelectedThread ?? conversationWorkflow.GetActiveThread(settings, GetCurrentScope());
    }

    private void RefreshProjectThreads(
        string? selectedThreadId = null,
        bool preserveCurrentSelection = true)
    {
        var scope = GetCurrentScope();
        if (preserveCurrentSelection)
        {
            selectedThreadId ??= SelectedThread?.ThreadId;
        }
        ProjectThreads.Clear();
        foreach (var thread in conversationWorkflow.GetThreads(settings, scope))
        {
            ProjectThreads.Add(thread);
        }

        SelectedThread = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, selectedThreadId, StringComparison.Ordinal));
        RefreshProjectNavigation();
    }

    private void HandleSelectedThreadChanged(ProjectThreadState? state)
    {
        if (state?.ScopeKind == ThreadScopeKind.General && !string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            CaptureAttachmentDraft(SelectedProjectPath, activeThreadId);
            CaptureReviewCommentDraft(SelectedProjectPath, activeThreadId);
            SelectedProjectPath = null;
            activeThreadId = null;
            activeTurnId = null;
            activeThreadLoaded = false;
            RefreshProjectThreads(state.ThreadId);
            return;
        }

        SelectThread(state);
        OnPropertyChanged(nameof(SelectedThread));
        OnPropertyChanged(nameof(ActiveWorkspacePath));
        OnPropertyChanged(nameof(ActiveWorkspaceLabel));
        OnPropertyChanged(nameof(SupportsSkills));
        OnPropertyChanged(nameof(SupportsCodexSettings));
        OnPropertyChanged(nameof(SupportsCodeReview));
        NotifyCodexContextChanged();
        Terminal.RefreshContext();
        _ = Git.RefreshAsync();
    }

    private void SelectThread(ProjectThreadState? state)
    {
        var previousActiveThreadId = activeThreadId;
        CaptureAttachmentDraft(SelectedProjectPath, previousActiveThreadId);
        CaptureReviewCommentDraft(SelectedProjectPath, previousActiveThreadId);
        if (previousActiveThreadId is null && state is not null)
        {
            var scope = state.ScopeKey;
            var newThreadDraft = settings.ComposerAttachmentDrafts.FirstOrDefault(item =>
                scope.Matches(item.ScopeKind, item.ProjectPath) &&
                item.ThreadId is null);
            var existingThreadDraft = settings.ComposerAttachmentDrafts.Any(item =>
                scope.Matches(item.ScopeKind, item.ProjectPath) &&
                string.Equals(item.ThreadId, state.ThreadId, StringComparison.Ordinal));
            if (newThreadDraft is not null && !existingThreadDraft)
            {
                newThreadDraft.ThreadId = state.ThreadId;
            }
        }
        activeThreadId = state?.ThreadId;
        if (!string.Equals(previousActiveThreadId, activeThreadId, StringComparison.Ordinal))
        {
            SteeringText = string.Empty;
        }

        activeThreadLoaded = state is not null && conversationWorkflow.IsLoaded(state.ThreadId);
        activeTurnId = state is not null && conversationWorkflow.TryGetActiveTurn(state.ThreadId, out var turnId) ? turnId : null;
        TaskWorkspace.SubmittedPrompt = state?.Preview ?? string.Empty;
        IsTurnRunning = state is not null && conversationWorkflow.IsRunning(state.ThreadId);

        if (state is null)
        {
            TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.ResetActiveConversation());
        }
        else
        {
            if (!conversationWorkflow.HasThread(state.ThreadId))
            {
                conversationWorkflow.RestoreThread(state);
            }
            TaskWorkspace.ApplyConversationSnapshot(conversationWorkflow.GetSnapshot(state.ThreadId));
            if (!state.IsArchived)
            {
                conversationWorkflow.SetActiveThread(settings, state.ScopeKey, state.ThreadId);
            }
        }

        var hasCodexGoalSurface = enableGoalMode && state is not null && ResolveHarnessId(state) == HarnessId.Codex;
        TaskWorkspace.ResetGoalContext(hasCodexGoalSurface);
        if (hasCodexGoalSurface && state is not null && !state.IsArchived)
        {
            _ = LoadSelectedGoalAsync(state.ThreadId, appServerWarmUpCancellation.Token);
        }

        RestoreAttachmentDraft(SelectedProjectPath, activeThreadId);
        RestoreReviewCommentDraft(SelectedProjectPath, activeThreadId);

        OnPropertyChanged(nameof(TimelineItems));
        OnPropertyChanged(nameof(RawEvents));
        OnPropertyChanged(nameof(FinalResponse));
        RaiseThreadCommandStates();
        _ = SaveSettingsAfterSelectionAsync();
    }

    private async Task SaveSettingsAfterSelectionAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "thread_selection_save_failed", "Could not save thread selection.", exception: ex);
        }
    }

    private async Task SaveActiveThreadStateAsync()
    {
        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            return;
        }

        try
        {
            await threadStatePersistence.SaveActiveAsync(
                settings,
                FindProjectThreadState(),
                GetCurrentScope(),
                activeThreadId,
                GetActiveWorkspacePath(),
                $"Thread {ProjectThreads.Count + 1}",
                cancellationToken: default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "thread_state_save_failed", "Could not persist thread state.", exception: ex);
        }
    }

    private void RefreshRecentProjects()
    {
        ProjectWorkspace.RefreshRecentProjects(settings.RecentProjects);
        RefreshProjectNavigation();
    }

    private void RefreshProjectNavigation()
    {
        var threads = new List<ProjectThreadState>();
        threads.AddRange(string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? ProjectThreads
            : conversationWorkflow.GetThreads(settings, ThreadScopeKey.General));
        foreach (var project in settings.RecentProjects)
        {
            if (ProjectNavigationItemViewModel.PathsEqual(project.Path, SelectedProjectPath))
            {
                threads.AddRange(ProjectThreads);
            }
            else
            {
                threads.AddRange(conversationWorkflow.GetProjectThreads(settings, project.Path));
            }
        }

        ProjectWorkspace.RefreshProjectNavigation(settings.RecentProjects, threads);
    }

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(CodexSummary));
        OnPropertyChanged(nameof(CodexExecutablePath));
        OnPropertyChanged(nameof(CodexVersion));
        OnPropertyChanged(nameof(AuthSummary));
        OnPropertyChanged(nameof(AuthDetail));
        OnPropertyChanged(nameof(CodexHome));
        OnPropertyChanged(nameof(SettingsPath));
        OnPropertyChanged(nameof(SupportsSkills));
        OnPropertyChanged(nameof(SupportsCodexSettings));
        DiagnosticsViewModel.RaiseCommandStates();
        RaiseThreadCommandStates();
    }

    private static bool IsSupportedImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    private static CodexConversationTurnSnapshot CloneConversationTurn(CodexConversationTurnSnapshot source) => new()
    {
        TurnId = source.TurnId,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        IsSuperseded = source.IsSuperseded,
        IsCodeReview = source.IsCodeReview,
        ReviewScope = source.ReviewScope,
        Activity = [.. source.Activity],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. source.GeneratedImagePaths],
        Diff = source.Diff
    };

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    private void RelayTerminalPropertyChanged(string? propertyName)
    {
        var mainProperty = propertyName switch
        {
            nameof(TerminalViewModel.Input) => nameof(TerminalInput),
            nameof(TerminalViewModel.Output) => nameof(TerminalOutput),
            nameof(TerminalViewModel.Status) => nameof(TerminalStatus),
            nameof(TerminalViewModel.WorkingDirectory) => nameof(TerminalWorkingDirectory),
            nameof(TerminalViewModel.IsRunning) => nameof(IsTerminalRunning),
            nameof(TerminalViewModel.IsVisible) => nameof(IsTerminalVisible),
            _ => null
        };
        if (mainProperty is not null)
        {
            OnPropertyChanged(mainProperty);
        }
    }

    private void UpdateSettingsSurfaceActivity()
    {
        Skills.IsActive = IsDetailsPaneOpen && SelectedInspectorTabIndex == 1;
        if (Skills.IsActive)
        {
            _ = EffectiveCodexSettings.RefreshIfStaleAsync(appServerWarmUpCancellation.Token);
        }
    }

    private void NotifyCodexContextChanged()
    {
        Skills.NotifyContextChanged();
        TaskWorkspace.SkillSelector.NotifyContextChanged();
        EffectiveCodexSettings.NotifyContextChanged();
        if (Skills.IsActive)
        {
            _ = EffectiveCodexSettings.RefreshIfStaleAsync(appServerWarmUpCancellation.Token);
        }
    }

    private async Task<ComposerSkillLoadResult> LoadComposerSkillsAsync(
        CancellationToken cancellationToken)
    {
        if (Skills.IsStale)
        {
            await Skills.RefreshAsync(forceReload: false, cancellationToken).ConfigureAwait(true);
        }

        IReadOnlyList<CodexSkillMetadata> enabledSkills = Skills.IsStale
            ? []
            : Skills.GetEnabledSkillSnapshot();
        return new ComposerSkillLoadResult(
            enabledSkills,
            Skills.IsSupported,
            Skills.Message);
    }

    private void RelayGitPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(GitViewModel.ReviewComments))
        {
            _ = SaveReviewCommentDraftAsync();
            TaskWorkspace.RaiseCommandStates();
        }

        var mainProperty = propertyName switch
        {
            nameof(GitViewModel.Branch) => nameof(GitBranch),
            nameof(GitViewModel.StatusMessage) => nameof(GitStatusMessage),
            nameof(GitViewModel.IsRepository) => nameof(IsGitRepository),
            nameof(GitViewModel.SelectedFile) => nameof(SelectedGitFile),
            nameof(GitViewModel.SelectedDiff) => nameof(SelectedDiff),
            nameof(GitViewModel.DiffViewLabel) => nameof(DiffViewLabel),
            nameof(GitViewModel.CommitMessage) => nameof(CommitMessage),
            nameof(GitViewModel.IsBusy) => nameof(IsGitBusy),
            _ => null
        };
        if (mainProperty is not null)
        {
            OnPropertyChanged(mainProperty);
            if (propertyName == nameof(GitViewModel.IsRepository))
            {
                OnPropertyChanged(nameof(SupportsCodeReview));
                TaskWorkspace.RaiseCommandStates();
            }
        }
    }

    private void RelayProjectPropertyChanged(string? propertyName)
    {
        var mainProperty = propertyName switch
        {
            nameof(ProjectThreadViewModel.SelectedProjectPath) => nameof(SelectedProjectPath),
            nameof(ProjectThreadViewModel.SelectedProjectName) => nameof(SelectedProjectName),
            nameof(ProjectThreadViewModel.NewThreadWorkspaceMode) => nameof(NewThreadWorkspaceMode),
            nameof(ProjectThreadViewModel.SelectedThread) => nameof(SelectedThread),
            nameof(ProjectThreadViewModel.ActiveWorkspacePath) => nameof(ActiveWorkspacePath),
            nameof(ProjectThreadViewModel.ActiveWorkspaceLabel) => nameof(ActiveWorkspaceLabel),
            _ => null
        };
        if (mainProperty is not null)
        {
            OnPropertyChanged(mainProperty);
            if (propertyName is nameof(ProjectThreadViewModel.SelectedProjectPath) or
                nameof(ProjectThreadViewModel.SelectedThread))
            {
                OnPropertyChanged(nameof(SupportsCodeReview));
                TaskWorkspace.RaiseCommandStates();
            }
            if (propertyName is nameof(ProjectThreadViewModel.SelectedProjectPath) or
                nameof(ProjectThreadViewModel.ActiveWorkspacePath) or
                nameof(ProjectThreadViewModel.ActiveWorkspaceLabel))
            {
                NotifyCodexContextChanged();
            }
        }
    }

    private void RelayTaskPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(TaskViewModel.ModelOverride) or
            nameof(TaskViewModel.ReasoningEffortOverride) or
            nameof(TaskViewModel.ServiceTierSelection))
        {
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            settings.LastServiceTierOverride = ToSettingsValue(TaskWorkspace.ServiceTierSelection);
            _ = SaveModelPreferencesAsync();
        }

        if (propertyName == nameof(TaskViewModel.FollowUpBehavior))
        {
            settings.FollowUpBehavior = TaskWorkspace.FollowUpBehavior.ToSettingsValue();
            _ = SaveFollowUpPreferenceAsync();
        }

        if (propertyName == nameof(TaskViewModel.Attachments))
        {
            _ = SaveAttachmentDraftAsync();
        }

        if (propertyName == nameof(TaskViewModel.ConversationTurns))
        {
            Git.SetReviewFindings(CodexReviewFindingProjection.GetLatest(TaskWorkspace.ConversationTurns));
            Git.SetLastTurnDiff(TaskWorkspace.ConversationTurns.LastOrDefault(turn => !turn.IsSuperseded)?.Diff);
        }

        var mainProperty = propertyName switch
        {
            nameof(TaskViewModel.Prompt) => nameof(PromptText),
            nameof(TaskViewModel.ModelOverride) => nameof(ModelOverride),
            nameof(TaskViewModel.ReasoningEffortOverride) => nameof(ReasoningEffortOverride),
            nameof(TaskViewModel.SteeringText) => nameof(SteeringText),
            nameof(TaskViewModel.AppServerHealth) => nameof(AppServerHealth),
            nameof(TaskViewModel.FinalResponse) => nameof(FinalResponse),
            nameof(TaskViewModel.TimelineItems) => nameof(TimelineItems),
            nameof(TaskViewModel.RawEvents) => nameof(RawEvents),
            nameof(TaskViewModel.IsTurnRunning) => nameof(IsTurnRunning),
            _ => null
        };
        if (mainProperty is not null)
        {
            OnPropertyChanged(mainProperty);
            if (propertyName == nameof(TaskViewModel.IsTurnRunning))
            {
                OnPropertyChanged(nameof(CanChangeExecutionPolicy));
            }
        }
    }

    private async Task SaveModelPreferencesAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "model_preferences_save_failed",
                "Could not save model preferences.",
                exception: ex);
        }
    }

    private async Task SaveFollowUpPreferenceAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "follow_up_preference_save_failed",
                "Could not save the follow-up behavior preference.",
                exception: ex);
        }
    }

    private static CodexServiceTierSelection ParseServiceTierSelection(string? value) =>
        NormalizeOverride(value)?.ToLowerInvariant() switch
        {
            "fast" => CodexServiceTierSelection.Fast,
            "standard" => CodexServiceTierSelection.Standard,
            _ => CodexServiceTierSelection.Inherit
        };

    private static string? ToSettingsValue(CodexServiceTierSelection selection) => selection switch
    {
        CodexServiceTierSelection.Inherit => null,
        CodexServiceTierSelection.Standard => "standard",
        CodexServiceTierSelection.Fast => "fast",
        _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, "Unknown service tier selection.")
    };

    // These capability adapters keep the shell from becoming the presentation
    // workspace's service locator.  Each exposes only the commands its workspace
    // needs; UI projection remains in MainViewModel.
    private sealed class TurnExecutionActionAdapter(MainViewModel owner) : ITurnExecutionActions
    {
        public Task SubmitAsync() => owner.SubmitPromptAsync();
        public Task CancelAsync() => owner.CancelTurnAsync();
        public Task SteerAsync() => owner.SteerTurnAsync();
        public bool CanSubmitTurn() => owner.CanSubmitTurn();
        public bool CanCancelTurn() => owner.CanCancelTurn();
        public bool CanSteerTurn() => owner.CanSteerTurn();
    }

    private sealed class CodeReviewActionAdapter(MainViewModel owner) : ICodeReviewActions
    {
        public Task StartCodeReviewAsync() => owner.StartCodeReviewAsync();
        public bool CanStartCodeReview() => owner.CanStartCodeReview();
    }

    private sealed class FollowUpManagementActionAdapter(MainViewModel owner) : IFollowUpManagementActions
    {
        public void OpenExternalUri(Uri uri) => owner.userInteractionService.OpenExternalUri(uri);
        public Task SendAlternateFollowUpAsync() => owner.SendAlternateFollowUpAsync();
        public Task PersistFollowUpQueueAsync(IReadOnlyList<QueuedFollowUpSnapshot> snapshots) => owner.PersistSelectedFollowUpQueueAsync(snapshots);
        public Task SendQueuedFollowUpAsync(string followUpId) => owner.SendQueuedFollowUpNowAsync(followUpId);
    }

    private sealed class ConversationHistoryActionAdapter(MainViewModel owner) : IConversationHistoryActions
    {
        public Task<bool> EditPromptAsync(CodexConversationTurn turn, string editedPrompt) => owner.EditPromptAsync(turn, editedPrompt);
        public Task ForkConversationAsync(string turnId) => owner.ForkConversationFromTurnAsync(turnId);
        public bool CanForkConversation() => owner.CanForkSelectedThread();
    }

    private sealed class AgentManagementActionAdapter(MainViewModel owner) : IAgentManagementActions
    {
        public Task<CodexThreadReadResult> ReadAgentThreadAsync(string threadId) => owner.ReadAgentThreadAsync(threadId);
        public Task SteerAgentAsync(string threadId, string turnId, string message) => owner.SteerAgentAsync(threadId, turnId, message);
        public Task StopAgentAsync(string threadId, string turnId) => owner.StopAgentAsync(threadId, turnId);
    }

    private sealed class GoalManagementActionAdapter(MainViewModel owner) : IGoalManagementActions
    {
        public Task<CodexThreadGoal> SetGoalAsync(string objective) => owner.SetSelectedGoalAsync(objective);
        public Task<CodexThreadGoal> SetGoalStatusAsync(CodexThreadGoalStatus status) => owner.SetSelectedGoalStatusAsync(status);
        public Task<bool> ClearGoalAsync() => owner.ClearSelectedGoalAsync();
        public Task StartGoalWorkAsync(string threadId, string objective) => owner.StartSelectedGoalWorkAsync(threadId, objective);
        public bool CanManageGoal() => owner.CanManageSelectedGoal();
    }

    private sealed class ComposerSupportActionAdapter(MainViewModel owner) : IComposerSupportActions
    {
        public Task LoadModelsAsync() => owner.LoadModelOptionsAsync();
        public bool CanLoadModels() => owner.CanLoadModels();
        public void ShowImagePreview(string path) => owner.userInteractionService.ShowImagePreview(path);
        public Task EditGeneratedImageAsync(string path) => owner.BeginGeneratedImageEditAsync(path);
        public Task<ComposerSkillLoadResult> LoadComposerSkillsAsync(CancellationToken cancellationToken) => owner.LoadComposerSkillsAsync(cancellationToken);
    }

    private sealed class ProjectNavigationActionAdapter(MainViewModel owner) : IProjectNavigationActions
    {
        public Task BrowseProjectAsync() => owner.BrowseProjectAsync();
        public Task OpenRecentProjectAsync(object? parameter) => owner.OpenRecentProjectAsync(parameter);
        public Task EditProjectAsync(object? parameter) => owner.EditProjectAsync(parameter);
        public Task CreateThreadAsync() => owner.NewThreadForCurrentScopeAsync();
        public Task CreateGeneralThreadAsync() => owner.NewGeneralThreadAsync();
        public Task CreateProjectThreadAsync() => owner.NewProjectThreadAsync();
        public bool CanCreateThread() => owner.CanCreateThreadInCurrentScope();
        public bool CanCreateGeneralThread() => owner.CanCreateGeneralThread();
        public bool CanEditProject(object? parameter) => owner.CanEditProject(parameter);
        public void SelectedThreadChanged(ProjectThreadState? state) => owner.HandleSelectedThreadChanged(state);
    }

    private sealed class ThreadLifecycleActionAdapter(MainViewModel owner) : IThreadLifecycleActions
    {
        public Task ResumeThreadAsync() => owner.ResumeSelectedThreadAsync();
        public Task ForkThreadAsync() => owner.ForkSelectedThreadAsync();
        public Task ArchiveThreadAsync() => owner.ArchiveSelectedThreadAsync();
        public Task UnarchiveThreadAsync() => owner.UnarchiveSelectedThreadAsync();
        public Task RemoveWorktreeAsync() => owner.RemoveSelectedWorktreeAsync();
        public bool CanUseSelectedThread() => owner.CanUseSelectedThread();
        public bool CanForkSelectedThread() => owner.CanForkSelectedThread();
        public bool CanArchiveSelectedThread() => owner.CanArchiveSelectedThread();
        public bool CanUnarchiveSelectedThread() => owner.CanUnarchiveSelectedThread();
        public bool CanRemoveSelectedWorktree() => owner.CanRemoveSelectedWorktree();
        public Task TogglePinThreadAsync() => owner.ToggleSelectedThreadPinAsync();
        public Task DeleteThreadAsync() => owner.DeleteSelectedThreadAsync();
        public bool CanTogglePinThread() => owner.CanToggleSelectedThreadPin();
        public bool CanDeleteThread() => owner.CanDeleteSelectedThread();
        public Task RenameThreadAsync() => owner.RenameSelectedThreadAsync();
        public bool CanRenameThread() => owner.CanRenameSelectedThread();
    }

    private readonly record struct CodexInstructionSnapshot(
        string? DeveloperInstructions,
        string? BaseInstructions);
}
