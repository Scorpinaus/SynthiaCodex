using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Git;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Core.Terminal;
using NativeCodexAssistant.Core.Worktrees;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly IAppServerSessionCoordinator appServerSessionCoordinator;
    private readonly IGitService gitService;
    private readonly IWorktreeService worktreeService;
    private readonly IRecentProjectService recentProjectService;
    private readonly IFolderPicker folderPicker;
    private readonly IUserInteractionService userInteractionService;
    private readonly IThemeService themeService;
    private readonly ThreadStore threadStore;
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly IAppLogger logger;
    private readonly CancellationTokenSource appServerWarmUpCancellation = new();
    private CodexThreadService threadService
    {
        get => TaskWorkspace.ThreadService;
        set => TaskWorkspace.UseThreadService(value);
    }
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
    private readonly AsyncRelayCommand steerTurnCommand;
    private readonly AsyncRelayCommand removeWorktreeCommand;
    private readonly RelayCommand toggleProjectRailCommand;
    private readonly RelayCommand toggleDetailsPaneCommand;

    private AppSettings settings = new();
    private CodexInstallation currentCodex => DiagnosticsViewModel.Installation;
    private AuthenticationState currentAuth => DiagnosticsViewModel.Authentication;
    private string? activeThreadId;
    private string? activeTurnId;
    private string selectedTheme = "System";
    private string statusMessage = "Starting";
    private bool isProjectRailOpen = true;
    private bool isDetailsPaneOpen;
    private bool isCompactLayout;
    private double viewportWidth = 1240;
    private int selectedWorkspaceTabIndex;
    private bool activeThreadLoaded;
    private bool isShuttingDown;
    private Task? shutdownTask;
    private Task? appServerWarmUpTask;
    private readonly HashSet<string> loadedThreadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> runningThreadIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeTurnIds = new(StringComparer.Ordinal);

    public MainViewModel(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        IAppServerSessionCoordinator appServerSessionCoordinator,
        IAuthService authService,
        IGitService gitService,
        IWorktreeService worktreeService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IUserInteractionService userInteractionService,
        IThemeService themeService,
        ICodexCliUtilityRunner codexCliUtilityRunner,
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace,
        ITerminalService terminalService,
        IAppLogger logger)
    {
        this.settingsStore = settingsStore;
        this.appServerSessionCoordinator = appServerSessionCoordinator;
        this.gitService = gitService;
        this.worktreeService = worktreeService;
        this.recentProjectService = recentProjectService;
        this.folderPicker = folderPicker;
        this.userInteractionService = userInteractionService;
        this.themeService = themeService;
        this.threadStore = threadStore;
        this.threadWorkspace = threadWorkspace;
        this.logger = logger;
        synchronizationContext = SynchronizationContext.Current;
        appServerSessionCoordinator.NotificationReceived += OnAppServerNotificationReceived;
        appServerSessionCoordinator.ConnectionFailed += OnAppServerConnectionFailed;
        appServerSessionCoordinator.StateChanged += OnAppServerStateChanged;

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

        Git = new GitViewModel(
            gitService,
            userInteractionService,
            logger,
            CreateGitContext,
            () => IsShuttingDown,
            message => StatusMessage = message);
        Git.PropertyChanged += (_, args) => RelayGitPropertyChanged(args.PropertyName);

        TaskWorkspace = new TaskViewModel(
            SubmitPromptAsync,
            CancelTurnAsync,
            LoadModelOptionsAsync,
            SteerTurnAsync,
            CanCancelTurn,
            CanSteerTurn,
            userInteractionService.OpenExternalUri);
        TaskWorkspace.PropertyChanged += (_, args) => RelayTaskPropertyChanged(args.PropertyName);

        ProjectWorkspace = new ProjectThreadViewModel(
            BrowseProjectAsync,
            OpenRecentProjectAsync,
            NewThreadAsync,
            ResumeSelectedThreadAsync,
            ForkSelectedThreadAsync,
            ArchiveSelectedThreadAsync,
            UnarchiveSelectedThreadAsync,
            RemoveSelectedWorktreeAsync,
            CanManageThreads,
            CanUseSelectedThread,
            CanArchiveSelectedThread,
            CanUnarchiveSelectedThread,
            CanRemoveSelectedWorktree,
            HandleSelectedThreadChanged);
        ProjectWorkspace.PropertyChanged += (_, args) => RelayProjectPropertyChanged(args.PropertyName);

        BrowseProjectCommand = ProjectWorkspace.BrowseProjectCommand;
        RefreshDiagnosticsCommand = DiagnosticsViewModel.RefreshCommand;
        RunCodexDoctorCommand = DiagnosticsViewModel.RunDoctorCommand;
        NewThreadCommand = newThreadCommand = (AsyncRelayCommand)ProjectWorkspace.NewThreadCommand;
        ResumeThreadCommand = resumeThreadCommand = (AsyncRelayCommand)ProjectWorkspace.ResumeThreadCommand;
        ForkThreadCommand = forkThreadCommand = (AsyncRelayCommand)ProjectWorkspace.ForkThreadCommand;
        ArchiveThreadCommand = archiveThreadCommand = (AsyncRelayCommand)ProjectWorkspace.ArchiveThreadCommand;
        UnarchiveThreadCommand = unarchiveThreadCommand = (AsyncRelayCommand)ProjectWorkspace.UnarchiveThreadCommand;
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
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<RecentProject> RecentProjects => ProjectWorkspace.RecentProjects;

    public ObservableCollection<string> Diagnostics => DiagnosticsViewModel.Lines;

    public ObservableCollection<CodexTimelineItem> TimelineItems => TaskWorkspace.TimelineItems;

    public ObservableCollection<string> RawEvents => TaskWorkspace.RawEvents;

    public ObservableCollection<string> ModelOptions => TaskWorkspace.ModelOptions;

    public ObservableCollection<GitChangedFile> ChangedFiles => Git.ChangedFiles;

    public ObservableCollection<ProjectThreadState> ProjectThreads => ProjectWorkspace.Threads;

    public TerminalViewModel Terminal { get; }

    public DiagnosticsViewModel DiagnosticsViewModel { get; }

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
        private set => SetProperty(ref isProjectRailOpen, value);
    }

    public bool IsDetailsPaneOpen
    {
        get => isDetailsPaneOpen;
        private set => SetProperty(ref isDetailsPaneOpen, value);
    }

    public bool IsCompactLayout
    {
        get => isCompactLayout;
        private set => SetProperty(ref isCompactLayout, value);
    }

    public int SelectedWorkspaceTabIndex
    {
        get => selectedWorkspaceTabIndex;
        set => SetProperty(ref selectedWorkspaceTabIndex, Math.Clamp(value, 0, 2));
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
            ProjectWorkspace.RaiseCommandStates();
        }
    }

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
            }
        }
    }

    public async Task InitializeAsync()
    {
        logger.Log(AppLogLevel.Information, "view_model_initialize", "Main view model initialization started.");
        settings = await settingsStore.LoadAsync().ConfigureAwait(true);
        IsProjectRailOpen = settings.IsProjectRailOpen;
        IsDetailsPaneOpen = settings.IsDetailsPaneOpen;
        selectedTheme = NormalizeTheme(settings.Theme);
        OnPropertyChanged(nameof(SelectedTheme));
        themeService.ApplyTheme(selectedTheme);
        ModelOverride = settings.LastModelOverride ?? string.Empty;
        ReasoningEffortOverride = settings.LastReasoningEffortOverride ?? string.Empty;
        RefreshRecentProjects();
        await DiagnosticsViewModel.RefreshAsync().ConfigureAwait(true);
        StatusMessage = "Ready";
        appServerWarmUpTask = WarmUpAppServerAsync(appServerWarmUpCancellation.Token);
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

    private async Task SelectProjectAsync(string path)
    {
        SelectedProjectPath = Path.GetFullPath(path);
        activeThreadId = null;
        activeTurnId = null;
        activeThreadLoaded = false;
        RestorePersistedThreadState();
        OnPropertyChanged(nameof(FinalResponse));
        recentProjectService.AddRecentProject(settings, SelectedProjectPath);
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

    private async Task NewThreadAsync()
    {
        if (!CanManageThreads())
        {
            StatusMessage = "Select a project and sign in before creating a thread";
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var result = await appServerSessionCoordinator.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
            AssistantWorktree? worktree = null;
            if (string.Equals(NewThreadWorkspaceMode, "New worktree", StringComparison.Ordinal))
            {
                var repository = await gitService.GetRepositoryStateAsync(SelectedProjectPath!).ConfigureAwait(true);
                if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
                {
                    throw new InvalidOperationException("A new worktree requires a detected Git repository.");
                }

                worktree = await worktreeService.CreateAsync(new WorktreeCreateRequest(
                    repository.RootPath,
                    $"thread-{ProjectThreads.Count + 1}",
                    result.ThreadId)).ConfigureAwait(true);
            }

            var state = CreateThreadState(
                result.ThreadId,
                $"Thread {ProjectThreads.Count + 1}",
                worktree?.Path,
                worktree?.Branch);
            loadedThreadIds.Add(result.ThreadId);
            RefreshProjectThreads(result.ThreadId);
            StatusMessage = worktree is null
                ? "New Codex thread created in the current checkout"
                : $"New Codex thread created in worktree {worktree.TaskId}";
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_create_failed", "Could not create a Codex thread.", exception: ex);
        }
    }

    private async Task ResumeSelectedThreadAsync()
    {
        if (!CanUseSelectedThread() || SelectedProjectPath is null || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var workspacePath = GetActiveWorkspacePath();
            var result = await appServerSessionCoordinator.ResumeThreadAsync(new CodexThreadResumeRequest(
                SelectedThread.ThreadId,
                workspacePath,
                CodexSandbox.WorkspaceWrite,
                NormalizeOverride(ModelOverride))).ConfigureAwait(true);
            threadService.ReconcileHistory(result.Turns ?? []);
            TaskWorkspace.NotifyResponseChanged();
            loadedThreadIds.Add(result.ThreadId);
            activeThreadLoaded = true;
            StatusMessage = "Codex thread resumed";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_resume_failed", "Could not resume the selected thread.", exception: ex);
        }
    }

    private async Task ForkSelectedThreadAsync()
    {
        if (!CanUseSelectedThread() || SelectedProjectPath is null || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var sourceThread = SelectedThread;
            var sourceWorkspace = GetActiveWorkspacePath();
            var result = await appServerSessionCoordinator.ForkThreadAsync(new CodexThreadForkRequest(
                sourceThread.ThreadId,
                sourceWorkspace,
                CodexSandbox.WorkspaceWrite,
                NormalizeOverride(ModelOverride))).ConfigureAwait(true);
            AssistantWorktree? worktree = null;
            if (string.Equals(sourceThread.Mode, "worktree", StringComparison.OrdinalIgnoreCase))
            {
                var repository = await gitService.GetRepositoryStateAsync(SelectedProjectPath).ConfigureAwait(true);
                if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
                {
                    throw new InvalidOperationException("The source project is no longer a Git repository.");
                }

                worktree = await worktreeService.CreateAsync(new WorktreeCreateRequest(
                    repository.RootPath,
                    $"fork-{result.ThreadId}",
                    result.ThreadId,
                    sourceThread.WorktreeBranch ?? "HEAD")).ConfigureAwait(true);
            }

            var state = CreateThreadState(
                result.ThreadId,
                $"Fork of {sourceThread.DisplayTitle}",
                worktree?.Path,
                worktree?.Branch);
            state.Preview = sourceThread.Preview;
            var sourceService = threadWorkspace.GetRequired(sourceThread.ThreadId);
            state.FinalResponse = sourceService.FinalResponse;
            state.ConversationTurns = sourceService.SnapshotConversation().Select(CloneConversationTurn).ToList();
            threadStore.Upsert(settings, state);
            loadedThreadIds.Add(result.ThreadId);
            RefreshProjectThreads(result.ThreadId);
            StatusMessage = "Codex thread forked";
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_fork_failed", "Could not fork the selected thread.", exception: ex);
        }
    }

    private async Task ArchiveSelectedThreadAsync()
    {
        if (!CanArchiveSelectedThread() || SelectedThread is null || SelectedProjectPath is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            await appServerSessionCoordinator.ArchiveThreadAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            await Terminal.StopAndRemoveAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            threadStore.SetArchived(settings, SelectedProjectPath, SelectedThread.ThreadId, archived: true);
            StatusMessage = "Codex thread archived";
            RefreshProjectThreads(SelectedThread.ThreadId);
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_archive_failed", "Could not archive the selected thread.", exception: ex);
        }
    }

    private async Task UnarchiveSelectedThreadAsync()
    {
        if (!CanUnarchiveSelectedThread() || SelectedThread is null || SelectedProjectPath is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            await appServerSessionCoordinator.UnarchiveThreadAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            threadStore.SetArchived(settings, SelectedProjectPath, SelectedThread.ThreadId, archived: false);
            StatusMessage = "Codex thread unarchived";
            RefreshProjectThreads(SelectedThread.ThreadId);
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "thread_unarchive_failed", "Could not unarchive the selected thread.", exception: ex);
        }
    }

    private async Task SteerTurnAsync()
    {
        if (!CanSteerTurn() || appServerSessionCoordinator.State != AppServerSessionState.Connected || activeThreadId is null || activeTurnId is null)
        {
            return;
        }

        try
        {
            var guidance = SteeringText.Trim();
            await appServerSessionCoordinator.SteerTurnAsync(new CodexTurnSteerRequest(
                activeThreadId,
                activeTurnId,
                guidance)).ConfigureAwait(true);
            threadService.AddGuidance(guidance);
            TaskWorkspace.NotifyResponseChanged();
            SteeringText = string.Empty;
            StatusMessage = "Steering sent to active turn";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "turn_steer_failed", "Could not steer the active turn.", exception: ex);
        }
    }

    private ProjectThreadState CreateThreadState(
        string threadId,
        string title,
        string? workspacePath = null,
        string? worktreeBranch = null)
    {
        var projectPath = SelectedProjectPath
            ?? throw new InvalidOperationException("A project must be selected before creating a thread record.");
        var state = new ProjectThreadState
        {
            ProjectPath = projectPath,
            ThreadId = threadId,
            Title = title,
            Mode = string.IsNullOrWhiteSpace(workspacePath) ? "local" : "worktree",
            WorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? projectPath : Path.GetFullPath(workspacePath),
            WorktreeBranch = worktreeBranch,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        threadStore.Upsert(settings, state);
        threadStore.SetActive(settings, projectPath, threadId);
        threadWorkspace.Restore(state);
        return state;
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
            var repository = await gitService.GetRepositoryStateAsync(SelectedProjectPath).ConfigureAwait(true);
            if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
            {
                throw new InvalidOperationException("The selected project is no longer a Git repository.");
            }

            await worktreeService.RemoveAsync(repository.RootPath, worktreePath).ConfigureAwait(true);
            thread.Mode = "worktree-removed";
            thread.TurnStatus = "Workspace removed";
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
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
        IsCompactLayout = width < 1000;
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
        if (IsProjectRailOpen && viewportWidth < 1000)
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
        if (IsDetailsPaneOpen && viewportWidth < 1000)
        {
            IsProjectRailOpen = false;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = IsDetailsPaneOpen;
        _ = SaveLayoutSelectionAsync();
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
        var path = SelectedThread?.WorkspacePath ?? SelectedProjectPath;
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
            ? null
            : $"project:{Path.GetFullPath(SelectedProjectPath)}";
        return new TerminalContext(key, workspacePath);
    }

    private GitContext CreateGitContext() => new(
        SelectedProjectPath,
        GetActiveWorkspacePathIfAvailable());

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
            StatusMessage = "A Codex turn is already running";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            StatusMessage = "Select a project before starting a Codex task";
            return;
        }

        if (string.IsNullOrWhiteSpace(PromptText))
        {
            StatusMessage = "Enter a prompt before starting a Codex task";
            return;
        }

        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before starting a task";
            return;
        }

        if (currentAuth.Readiness is AuthReadiness.Unavailable or AuthReadiness.NotSignedIn)
        {
            StatusMessage = "Sign in with Codex before starting a task";
            return;
        }

        StatusMessage = "Starting Codex task";

        try
        {
            var submittedPrompt = PromptText.Trim();
            TaskWorkspace.SubmittedPrompt = submittedPrompt;
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            activeThreadId = await EnsureActiveThreadAsync().ConfigureAwait(true);
            threadService.BeginTurn(submittedPrompt);
            TaskWorkspace.NotifyResponseChanged();
            if (SelectedThread is not null)
            {
                SelectedThread.Preview = submittedPrompt;
            }

            var persistedThread = settings.ProjectThreads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, activeThreadId, StringComparison.Ordinal));
            if (persistedThread is not null)
            {
                persistedThread.Preview = submittedPrompt;
            }

            var workspacePath = GetActiveWorkspacePath();
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            var turn = await appServerSessionCoordinator.StartTurnAsync(new CodexTurnStartRequest(
                activeThreadId,
                submittedPrompt,
                workspacePath,
                CodexSandbox.WorkspaceWrite,
                NormalizeOverride(ModelOverride),
                ParseReasoningEffort(ReasoningEffortOverride))).ConfigureAwait(true);

            var boundTurn = threadService.BindPendingTurn(turn.TurnId);
            threadWorkspace.RegisterTurn(activeThreadId, turn.TurnId);
            if (boundTurn.Status == CodexTurnStatus.Running)
            {
                runningThreadIds.Add(activeThreadId);
                UpdateThreadActivity(activeThreadId, isRunning: true, "Running");
                IsTurnRunning = true;
                activeTurnId = turn.TurnId;
                activeTurnIds[activeThreadId] = turn.TurnId;
            }
            else
            {
                activeTurnId = null;
                activeTurnIds.Remove(activeThreadId);
                runningThreadIds.Remove(activeThreadId);
                IsTurnRunning = false;
            }
            cancelTurnCommand.RaiseCanExecuteChanged();
            StatusMessage = boundTurn.Status == CodexTurnStatus.Running
                ? "Codex turn running"
                : $"Codex turn {boundTurn.Status.ToString().ToLowerInvariant()}";
        }
        catch (Exception ex)
        {
            threadService.FailPendingTurn(ex.Message);
            TaskWorkspace.NotifyResponseChanged();
            if (!string.IsNullOrWhiteSpace(activeThreadId))
            {
                runningThreadIds.Remove(activeThreadId);
                activeTurnIds.Remove(activeThreadId);
            }
            IsTurnRunning = false;
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_task_start_failed", "Could not start Codex task.", exception: ex);
        }
    }

    private async Task<string> EnsureActiveThreadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            throw new InvalidOperationException("A project must be selected before starting a Codex thread.");
        }

        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            var thread = await appServerSessionCoordinator.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
            AssistantWorktree? worktree = null;
            if (string.Equals(NewThreadWorkspaceMode, "New worktree", StringComparison.Ordinal))
            {
                var repository = await gitService.GetRepositoryStateAsync(SelectedProjectPath).ConfigureAwait(true);
                if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
                {
                    throw new InvalidOperationException("A new worktree requires a detected Git repository.");
                }

                worktree = await worktreeService.CreateAsync(new WorktreeCreateRequest(
                    repository.RootPath,
                    $"thread-{ProjectThreads.Count + 1}",
                    thread.ThreadId)).ConfigureAwait(true);
            }

            CreateThreadState(
                thread.ThreadId,
                $"Thread {ProjectThreads.Count + 1}",
                worktree?.Path,
                worktree?.Branch);
            RefreshProjectThreads(thread.ThreadId);
            loadedThreadIds.Add(thread.ThreadId);
            activeThreadLoaded = true;
            return thread.ThreadId;
        }

        if (activeThreadLoaded)
        {
            return activeThreadId;
        }

        try
        {
            var resumed = await appServerSessionCoordinator.ResumeThreadAsync(new CodexThreadResumeRequest(
                activeThreadId,
                GetActiveWorkspacePath(),
                CodexSandbox.WorkspaceWrite)).ConfigureAwait(true);
            threadService.ReconcileHistory(resumed.Turns ?? []);
            TaskWorkspace.NotifyResponseChanged();
            activeThreadLoaded = true;
            loadedThreadIds.Add(resumed.ThreadId);
            StatusMessage = "Codex thread resumed";
            return resumed.ThreadId;
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "codex_thread_resume_failed",
                "Could not resume persisted Codex thread; starting a new thread.",
                new Dictionary<string, string?> { ["threadId"] = activeThreadId },
                ex);

            var thread = await appServerSessionCoordinator.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
            CreateThreadState(thread.ThreadId, $"Thread {ProjectThreads.Count + 1}");
            RefreshProjectThreads(thread.ThreadId);
            loadedThreadIds.Add(thread.ThreadId);
            activeThreadLoaded = true;
            StatusMessage = "Previous thread could not be resumed; started a new Codex thread";
            return thread.ThreadId;
        }
    }

    private async Task CancelTurnAsync()
    {
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

        if (!CanCancelTurn() || appServerSessionCoordinator.State != AppServerSessionState.Connected || string.IsNullOrWhiteSpace(activeThreadId) || string.IsNullOrWhiteSpace(activeTurnId))
        {
            StatusMessage = "No active turn to cancel";
            return;
        }

        try
        {
            await appServerSessionCoordinator.CancelTurnAsync(activeThreadId, activeTurnId).ConfigureAwait(true);
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
        var activeTurnsAtStart = activeTurnIds.Count > 0
            ? activeTurnIds.Count
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
        appServerSessionCoordinator.FlushNotifications();
        await appServerSessionCoordinator.DisposeAsync().ConfigureAwait(true);
        await SaveActiveThreadStateAsync().ConfigureAwait(true);

        IsTurnRunning = false;
        activeTurnId = null;
        activeTurnIds.Clear();
        runningThreadIds.Clear();
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
        if (appServerSessionCoordinator.State != AppServerSessionState.Connected)
        {
            return;
        }

        var turns = activeTurnIds.Count > 0
            ? activeTurnIds.ToArray()
            : !string.IsNullOrWhiteSpace(activeThreadId) && !string.IsNullOrWhiteSpace(activeTurnId)
                ? [new KeyValuePair<string, string>(activeThreadId, activeTurnId)]
                : [];

        foreach (var turn in turns)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await appServerSessionCoordinator.CancelTurnAsync(turn.Key, turn.Value, timeout.Token).ConfigureAwait(true);
                runningThreadIds.Remove(turn.Key);
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

        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before loading models";
            return;
        }

        if (currentAuth.Readiness is AuthReadiness.Unavailable or AuthReadiness.NotSignedIn)
        {
            StatusMessage = "Sign in with Codex before loading models";
            return;
        }

        loadModelsCommand.RaiseCanExecuteChanged();
        StatusMessage = "Loading Codex models";

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var models = await appServerSessionCoordinator.ListModelsAsync().ConfigureAwait(true);
            ModelOptions.Clear();
            foreach (var model in models.Select(model => model.Model).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(model => model))
            {
                ModelOptions.Add(model);
            }

            StatusMessage = ModelOptions.Count == 0
                ? "No Codex models returned"
                : $"Loaded {ModelOptions.Count} Codex models";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "codex_model_list_failed", "Could not load Codex model list.", exception: ex);
        }
    }

    private static string? NormalizeOverride(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static CodexReasoningEffort? ParseReasoningEffort(string? value)
    {
        return NormalizeOverride(value)?.ToLowerInvariant() switch
        {
            "none" => CodexReasoningEffort.None,
            "minimal" => CodexReasoningEffort.Minimal,
            "low" => CodexReasoningEffort.Low,
            "medium" => CodexReasoningEffort.Medium,
            "high" => CodexReasoningEffort.High,
            "xhigh" => CodexReasoningEffort.XHigh,
            _ => null
        };
    }

    private bool CanCancelTurn()
    {
        return !IsShuttingDown &&
            IsTurnRunning &&
            appServerSessionCoordinator.State == AppServerSessionState.Connected &&
            !string.IsNullOrWhiteSpace(activeThreadId) &&
            !string.IsNullOrWhiteSpace(activeTurnId);
    }

    private bool CanManageThreads() =>
        !IsShuttingDown &&
        !string.IsNullOrWhiteSpace(SelectedProjectPath) &&
        currentCodex.IsFound &&
        currentAuth.Readiness is not (AuthReadiness.Unavailable or AuthReadiness.NotSignedIn);

    private bool CanUseSelectedThread() => CanManageThreads() && SelectedThread is not null;

    private bool CanArchiveSelectedThread() =>
        CanUseSelectedThread() && SelectedThread?.IsArchived == false && !IsTurnRunning;

    private bool CanUnarchiveSelectedThread() =>
        CanUseSelectedThread() && SelectedThread?.IsArchived == true;

    private bool CanRemoveSelectedWorktree() =>
        CanUseSelectedThread() &&
        SelectedThread?.IsRunning == false &&
        string.Equals(SelectedThread.Mode, "worktree", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(SelectedThread.WorkspacePath);

    private bool CanSteerTurn() =>
        !IsShuttingDown &&
        IsTurnRunning &&
        appServerSessionCoordinator.State == AppServerSessionState.Connected &&
        !string.IsNullOrWhiteSpace(activeThreadId) &&
        !string.IsNullOrWhiteSpace(activeTurnId) &&
        !string.IsNullOrWhiteSpace(SteeringText);

    private void RaiseThreadCommandStates()
    {
        ProjectWorkspace.RaiseCommandStates();
        TaskWorkspace.RaiseCommandStates();
    }

    private string GetActiveWorkspacePath()
    {
        var path = SelectedThread?.WorkspacePath ?? SelectedProjectPath
            ?? throw new InvalidOperationException("Select a project before starting a Codex task.");
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"The active workspace is unavailable: {path}");
        }

        return path;
    }

    private async Task EnsureAppServerSessionAsync(CancellationToken cancellationToken = default)
    {
        await appServerSessionCoordinator.EnsureConnectedAsync(currentCodex, cancellationToken).ConfigureAwait(true);
    }

    private void OnAppServerConnectionFailed(object? sender, AppServerConnectionFailedEventArgs args)
    {
        void ApplyFailure()
        {
            foreach (var threadId in runningThreadIds.ToArray())
            {
                UpdateThreadActivity(threadId, isRunning: false, "Recovery needed");
            }

            runningThreadIds.Clear();
            activeTurnIds.Clear();
            loadedThreadIds.Clear();
            activeThreadLoaded = false;
            activeTurnId = null;
            IsTurnRunning = false;
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

    private void OnAppServerNotificationReceived(object? sender, AppServerNotification notification)
    {
        DispatchAppServerNotification(notification);
    }

    private void OnAppServerStateChanged(object? sender, AppServerSessionStateChangedEventArgs args)
    {
        void ApplyState() => AppServerHealth = args.State switch
        {
            AppServerSessionState.Connecting => "Codex connecting",
            AppServerSessionState.Connected => "Codex connected",
            AppServerSessionState.Reconnecting => "Codex reconnecting",
            AppServerSessionState.Unavailable => "Codex unavailable",
            AppServerSessionState.Disposed => "Codex stopped",
            _ => "Codex idle"
        };

        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplyState();
        }
        else
        {
            synchronizationContext.Post(_ => ApplyState(), null);
        }
    }

    private void DispatchAppServerNotification(AppServerNotification notification)
    {
        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplyNotification(notification);
            return;
        }

        synchronizationContext.Post(_ => ApplyNotification(notification), null);
    }

    private void ApplyNotification(AppServerNotification notification)
    {
        var routedThreadId = threadWorkspace.ApplyNotification(notification);
        if (string.IsNullOrWhiteSpace(routedThreadId))
        {
            threadService.ApplyNotification(notification);
            routedThreadId = activeThreadId;
        }

        var routedService = !string.IsNullOrWhiteSpace(routedThreadId) && threadWorkspace.ThreadIds.Contains(routedThreadId)
            ? threadWorkspace.GetRequired(routedThreadId)
            : threadService;
        if (!string.IsNullOrWhiteSpace(routedThreadId) && !string.IsNullOrWhiteSpace(routedService.ActiveTurnId))
        {
            activeTurnIds[routedThreadId] = routedService.ActiveTurnId;
            threadWorkspace.RegisterTurn(routedThreadId, routedService.ActiveTurnId);
        }

        if (string.Equals(routedThreadId, activeThreadId, StringComparison.Ordinal))
        {
            threadService = routedService;
            activeTurnId = routedService.ActiveTurnId ?? activeTurnId;
        }

        if (notification.Method == "turn/completed")
        {
            if (!string.IsNullOrWhiteSpace(routedThreadId))
            {
                runningThreadIds.Remove(routedThreadId);
                activeTurnIds.Remove(routedThreadId);
                UpdateThreadActivity(
                    routedThreadId,
                    isRunning: false,
                    routedService.ActiveTurnStatus.ToString());
            }

            if (string.Equals(routedThreadId, activeThreadId, StringComparison.Ordinal))
            {
                IsTurnRunning = false;
                activeTurnId = null;
            }

            if (routedService.ActiveTurnStatus == CodexTurnStatus.Completed)
            {
                if (string.Equals(routedThreadId, activeThreadId, StringComparison.Ordinal))
                {
                    PromptText = string.Empty;
                    StatusMessage = "Codex turn completed";
                }
            }
            else if (routedService.RequiresAuthentication)
            {
                StatusMessage = "Codex authentication failed. Sign in and retry.";
            }
            else
            {
                StatusMessage = $"Codex turn {routedService.ActiveTurnStatus.ToString().ToLowerInvariant()}";
            }

            if (!string.IsNullOrWhiteSpace(routedThreadId))
            {
                _ = SaveThreadStateAsync(routedThreadId, routedService);
            }
            _ = Git.RefreshAsync();
        }

        if (!string.IsNullOrWhiteSpace(routedThreadId) &&
            !string.IsNullOrWhiteSpace(SelectedProjectPath) &&
            notification.Method is "thread/archived" or "thread/unarchived" &&
            settings.ProjectThreads.Any(thread => string.Equals(thread.ThreadId, routedThreadId, StringComparison.Ordinal)))
        {
            threadStore.SetArchived(
                settings,
                SelectedProjectPath,
                routedThreadId,
                archived: notification.Method == "thread/archived");
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

    private async Task SaveThreadStateAsync(string threadId, CodexThreadService service)
    {
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted is null)
        {
            return;
        }

        persisted.FinalResponse = service.FinalResponse;
        persisted.TimelineItems = [.. service.TimelineItems.TakeLast(100)];
        persisted.RawEvents = [.. service.RawEvents.TakeLast(100)];
        persisted.ConversationTurns = service.SnapshotConversation().Select(CloneConversationTurn).ToList();
        persisted.UpdatedAt = DateTimeOffset.UtcNow;
        var presentation = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (presentation is not null)
        {
            presentation.FinalResponse = persisted.FinalResponse;
            presentation.TimelineItems = [.. persisted.TimelineItems];
            presentation.RawEvents = [.. persisted.RawEvents];
            presentation.ConversationTurns = persisted.ConversationTurns.Select(CloneConversationTurn).ToList();
            presentation.UpdatedAt = persisted.UpdatedAt;
        }
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "thread_state_save_failed", "Could not persist thread state.", exception: ex);
        }
    }

    private void RestorePersistedThreadState()
    {
        ProjectThreads.Clear();
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            threadService.Reset();
            SelectedThread = null;
            RefreshProjectNavigation();
            return;
        }

        foreach (var persisted in threadStore.GetProjectThreads(settings, SelectedProjectPath))
        {
            ProjectThreads.Add(persisted);
            threadWorkspace.Restore(persisted);
        }

        SelectedThread = threadStore.GetActive(settings, SelectedProjectPath);
        if (SelectedThread is null)
        {
            threadService = new CodexThreadService();
            threadService.Reset();
            OnPropertyChanged(nameof(TimelineItems));
            OnPropertyChanged(nameof(RawEvents));
            OnPropertyChanged(nameof(FinalResponse));
        }

        RefreshProjectNavigation();
    }

    private ProjectThreadState? FindProjectThreadState()
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            return null;
        }

        return SelectedThread ?? threadStore.GetActive(settings, SelectedProjectPath);
    }

    private void RefreshProjectThreads(string? selectedThreadId = null)
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            ProjectThreads.Clear();
            SelectedThread = null;
            return;
        }

        selectedThreadId ??= SelectedThread?.ThreadId;
        ProjectThreads.Clear();
        foreach (var thread in threadStore.GetProjectThreads(settings, SelectedProjectPath))
        {
            ProjectThreads.Add(thread);
        }

        SelectedThread = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, selectedThreadId, StringComparison.Ordinal));
        RefreshProjectNavigation();
    }

    private void HandleSelectedThreadChanged(ProjectThreadState? state)
    {
        SelectThread(state);
        OnPropertyChanged(nameof(SelectedThread));
        OnPropertyChanged(nameof(ActiveWorkspacePath));
        OnPropertyChanged(nameof(ActiveWorkspaceLabel));
        Terminal.RefreshContext();
        _ = Git.RefreshAsync();
    }

    private void SelectThread(ProjectThreadState? state)
    {
        var previousActiveThreadId = activeThreadId;
        activeThreadId = state?.ThreadId;
        if (!string.Equals(previousActiveThreadId, activeThreadId, StringComparison.Ordinal))
        {
            SteeringText = string.Empty;
        }

        activeThreadLoaded = state is not null && loadedThreadIds.Contains(state.ThreadId);
        activeTurnId = state is not null && activeTurnIds.TryGetValue(state.ThreadId, out var turnId) ? turnId : null;
        TaskWorkspace.SubmittedPrompt = state?.Preview ?? string.Empty;
        IsTurnRunning = state is not null && runningThreadIds.Contains(state.ThreadId);

        if (state is null)
        {
            threadService = new CodexThreadService();
            threadService.Reset();
        }
        else
        {
            threadService = threadWorkspace.ThreadIds.Contains(state.ThreadId)
                ? threadWorkspace.GetRequired(state.ThreadId)
                : threadWorkspace.Restore(state);
            if (!state.IsArchived && !string.IsNullOrWhiteSpace(SelectedProjectPath))
            {
                threadStore.SetActive(settings, SelectedProjectPath, state.ThreadId);
            }
        }

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
        if (string.IsNullOrWhiteSpace(SelectedProjectPath) || string.IsNullOrWhiteSpace(activeThreadId))
        {
            return;
        }

        try
        {
            var persisted = FindProjectThreadState();
            if (persisted is null)
            {
                persisted = new ProjectThreadState
                {
                    ProjectPath = SelectedProjectPath,
                    ThreadId = activeThreadId,
                    Title = $"Thread {ProjectThreads.Count + 1}"
                };
                threadStore.Upsert(settings, persisted);
            }

            persisted.ProjectPath = SelectedProjectPath;
            persisted.ThreadId = activeThreadId;
            persisted.FinalResponse = threadService.FinalResponse;
            persisted.TimelineItems = [.. threadService.TimelineItems.TakeLast(100)];
            persisted.RawEvents = [.. threadService.RawEvents.TakeLast(100)];
            persisted.ConversationTurns = threadService.SnapshotConversation().Select(CloneConversationTurn).ToList();
            persisted.UpdatedAt = DateTimeOffset.UtcNow;
            threadStore.Upsert(settings, persisted);

            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
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
        foreach (var project in settings.RecentProjects)
        {
            if (ProjectNavigationItemViewModel.PathsEqual(project.Path, SelectedProjectPath))
            {
                threads.AddRange(ProjectThreads);
            }
            else
            {
                threads.AddRange(threadStore.GetProjectThreads(settings, project.Path));
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
        DiagnosticsViewModel.RaiseCommandStates();
        RaiseThreadCommandStates();
    }

    private static CodexConversationTurnSnapshot CloneConversationTurn(CodexConversationTurnSnapshot source) => new()
    {
        TurnId = source.TurnId,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        Activity = [.. source.Activity]
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

    private void RelayGitPropertyChanged(string? propertyName)
    {
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
        }
    }

    private void RelayTaskPropertyChanged(string? propertyName)
    {
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
        }
    }
}
