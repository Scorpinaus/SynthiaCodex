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
using NativeCodexAssistant.Infrastructure.Codex;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly ICodexDiscoveryService codexDiscoveryService;
    private readonly ICodexProcessService codexProcessService;
    private readonly IAuthService authService;
    private readonly IGitService gitService;
    private readonly IWorktreeService worktreeService;
    private readonly IRecentProjectService recentProjectService;
    private readonly IFolderPicker folderPicker;
    private readonly IUserInteractionService userInteractionService;
    private readonly IThemeService themeService;
    private readonly ICodexCliUtilityRunner codexCliUtilityRunner;
    private readonly ThreadStore threadStore;
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly ITerminalService terminalService;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim appServerLifecycleGate = new(1, 1);
    private readonly CancellationTokenSource appServerWarmUpCancellation = new();
    private CodexThreadService threadService = new();
    private readonly SynchronizationContext? synchronizationContext;
    private readonly AsyncRelayCommand submitPromptCommand;
    private readonly AsyncRelayCommand cancelTurnCommand;
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly AsyncRelayCommand exitApplicationCommand;
    private readonly AsyncRelayCommand refreshGitCommand;
    private readonly AsyncRelayCommand runCodexDoctorCommand;
    private readonly AsyncRelayCommand newThreadCommand;
    private readonly AsyncRelayCommand resumeThreadCommand;
    private readonly AsyncRelayCommand forkThreadCommand;
    private readonly AsyncRelayCommand archiveThreadCommand;
    private readonly AsyncRelayCommand unarchiveThreadCommand;
    private readonly AsyncRelayCommand steerTurnCommand;
    private readonly AsyncRelayCommand removeWorktreeCommand;
    private readonly AsyncRelayCommand showWorkingDiffCommand;
    private readonly AsyncRelayCommand showStagedDiffCommand;
    private readonly AsyncRelayCommand stageSelectedFileCommand;
    private readonly AsyncRelayCommand unstageSelectedFileCommand;
    private readonly AsyncRelayCommand revertSelectedFileCommand;
    private readonly AsyncRelayCommand commitCommand;
    private readonly RelayCommand openInEditorCommand;
    private readonly RelayCommand revealInExplorerCommand;
    private readonly AsyncRelayCommand startTerminalCommand;
    private readonly AsyncRelayCommand sendTerminalInputCommand;
    private readonly AsyncRelayCommand killTerminalCommand;
    private readonly RelayCommand clearTerminalCommand;
    private readonly RelayCommand toggleTerminalCommand;
    private readonly RelayCommand toggleProjectRailCommand;
    private readonly RelayCommand toggleDetailsPaneCommand;

    private AppSettings settings = new();
    private CodexInstallation currentCodex = CodexInstallation.Missing("Detection has not run yet.");
    private AuthenticationState currentAuth = new(AuthReadiness.Unknown, "Checking sign-in", "Authentication detection has not run yet.", null);
    private string? selectedProjectPath;
    private string? activeThreadId;
    private string? activeTurnId;
    private string promptText = string.Empty;
    private string modelOverride = string.Empty;
    private string reasoningEffortOverride = string.Empty;
    private string selectedTheme = "System";
    private string newThreadWorkspaceMode = "Current checkout";
    private string steeringText = string.Empty;
    private string appServerHealth = "Codex idle";
    private string statusMessage = "Starting";
    private string? gitRepositoryRoot;
    private string gitBranch = "No repository";
    private string gitStatusMessage = "Select a project to inspect Git changes";
    private string selectedDiff = "Select a changed file to inspect its diff.";
    private string commitMessage = string.Empty;
    private string terminalInput = string.Empty;
    private string terminalOutput = string.Empty;
    private string terminalStatus = "Not started";
    private string terminalWorkingDirectory = "No terminal session";
    private GitChangedFile? selectedGitFile;
    private ProjectThreadState? selectedThread;
    private bool isBusy;
    private bool isGitBusy;
    private bool isShowingStagedDiff;
    private bool isTurnRunning;
    private bool isTerminalVisible;
    private bool isProjectRailOpen = true;
    private bool isDetailsPaneOpen;
    private bool isCompactLayout;
    private double viewportWidth = 1240;
    private int selectedWorkspaceTabIndex;
    private bool activeThreadLoaded;
    private bool isShuttingDown;
    private Task? shutdownTask;
    private Task? appServerWarmUpTask;
    private CodexAppServerClient? appServerClient;
    private CodexCliUtilityResult? lastDoctorResult;
    private readonly HashSet<string> loadedThreadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> runningThreadIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeTurnIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalState> terminalStates = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        ICodexProcessService codexProcessService,
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
        this.codexDiscoveryService = codexDiscoveryService;
        this.codexProcessService = codexProcessService;
        this.authService = authService;
        this.gitService = gitService;
        this.worktreeService = worktreeService;
        this.recentProjectService = recentProjectService;
        this.folderPicker = folderPicker;
        this.userInteractionService = userInteractionService;
        this.themeService = themeService;
        this.codexCliUtilityRunner = codexCliUtilityRunner;
        this.threadStore = threadStore;
        this.threadWorkspace = threadWorkspace;
        this.terminalService = terminalService;
        this.logger = logger;
        synchronizationContext = SynchronizationContext.Current;

        BrowseProjectCommand = new AsyncRelayCommand(BrowseProjectAsync);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync);
        RunCodexDoctorCommand = runCodexDoctorCommand = new AsyncRelayCommand(RunCodexDoctorAsync, CanRunCodexDoctor);
        NewThreadCommand = newThreadCommand = new AsyncRelayCommand(NewThreadAsync, CanManageThreads);
        ResumeThreadCommand = resumeThreadCommand = new AsyncRelayCommand(ResumeSelectedThreadAsync, CanUseSelectedThread);
        ForkThreadCommand = forkThreadCommand = new AsyncRelayCommand(ForkSelectedThreadAsync, CanUseSelectedThread);
        ArchiveThreadCommand = archiveThreadCommand = new AsyncRelayCommand(ArchiveSelectedThreadAsync, CanArchiveSelectedThread);
        UnarchiveThreadCommand = unarchiveThreadCommand = new AsyncRelayCommand(UnarchiveSelectedThreadAsync, CanUnarchiveSelectedThread);
        SteerTurnCommand = steerTurnCommand = new AsyncRelayCommand(SteerTurnAsync, CanSteerTurn);
        RemoveWorktreeCommand = removeWorktreeCommand = new AsyncRelayCommand(RemoveSelectedWorktreeAsync, CanRemoveSelectedWorktree);
        OpenRecentProjectCommand = new AsyncRelayCommand(OpenRecentProjectAsync);
        SignInChatGptCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.ChatGpt));
        SignInDeviceCodeCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.DeviceCode));
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        SubmitPromptCommand = submitPromptCommand = new AsyncRelayCommand(SubmitPromptAsync);
        CancelTurnCommand = cancelTurnCommand = new AsyncRelayCommand(CancelTurnAsync, CanCancelTurn);
        LoadModelsCommand = loadModelsCommand = new AsyncRelayCommand(LoadModelOptionsAsync);
        ExitApplicationCommand = exitApplicationCommand = new AsyncRelayCommand(RequestApplicationExitAsync, () => !isShuttingDown);
        RefreshGitCommand = refreshGitCommand = new AsyncRelayCommand(RefreshGitAsync, CanUseGitProject);
        ShowWorkingDiffCommand = showWorkingDiffCommand = new AsyncRelayCommand(() => LoadSelectedDiffAsync(staged: false), CanShowWorkingDiff);
        ShowStagedDiffCommand = showStagedDiffCommand = new AsyncRelayCommand(() => LoadSelectedDiffAsync(staged: true), CanShowStagedDiff);
        StageSelectedFileCommand = stageSelectedFileCommand = new AsyncRelayCommand(StageSelectedFileAsync, CanStageSelectedFile);
        UnstageSelectedFileCommand = unstageSelectedFileCommand = new AsyncRelayCommand(UnstageSelectedFileAsync, CanUnstageSelectedFile);
        RevertSelectedFileCommand = revertSelectedFileCommand = new AsyncRelayCommand(RevertSelectedFileAsync, CanMutateSelectedFile);
        CommitCommand = commitCommand = new AsyncRelayCommand(CommitAsync, CanCommit);
        OpenInEditorCommand = openInEditorCommand = new RelayCommand(OpenInEditor, CanOpenProjectTarget);
        RevealInExplorerCommand = revealInExplorerCommand = new RelayCommand(RevealInExplorer, CanOpenProjectTarget);
        StartTerminalCommand = startTerminalCommand = new AsyncRelayCommand(StartTerminalAsync, CanStartTerminal);
        SendTerminalInputCommand = sendTerminalInputCommand = new AsyncRelayCommand(SendTerminalInputAsync, CanSendTerminalInput);
        KillTerminalCommand = killTerminalCommand = new AsyncRelayCommand(KillTerminalAsync, CanKillTerminal);
        ClearTerminalCommand = clearTerminalCommand = new RelayCommand(ClearTerminal, CanClearTerminal);
        ToggleTerminalCommand = toggleTerminalCommand = new RelayCommand(ToggleTerminal, () => !IsShuttingDown);
        ToggleProjectRailCommand = toggleProjectRailCommand = new RelayCommand(ToggleProjectRail, () => !IsShuttingDown);
        ToggleDetailsPaneCommand = toggleDetailsPaneCommand = new RelayCommand(ToggleDetailsPane, () => !IsShuttingDown);
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ObservableCollection<CodexTimelineItem> TimelineItems => threadService.TimelineItems;

    public ObservableCollection<string> RawEvents => threadService.RawEvents;

    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<GitChangedFile> ChangedFiles { get; } = [];

    public ObservableCollection<ProjectThreadState> ProjectThreads { get; } = [];

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

    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public IReadOnlyList<string> WorkspaceModeOptions { get; } = ["Current checkout", "New worktree"];

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
        get => selectedProjectPath;
        private set
        {
            if (SetProperty(ref selectedProjectPath, value))
            {
                OnPropertyChanged(nameof(SelectedProjectName));
                refreshGitCommand.RaiseCanExecuteChanged();
                openInEditorCommand.RaiseCanExecuteChanged();
                revealInExplorerCommand.RaiseCanExecuteChanged();
                RaiseThreadCommandStates();
                RefreshVisibleTerminal();
            }
        }
    }

    public string SelectedProjectName =>
        string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? "No project selected"
            : new DirectoryInfo(SelectedProjectPath).Name;

    public string NewThreadWorkspaceMode
    {
        get => newThreadWorkspaceMode;
        set => SetProperty(ref newThreadWorkspaceMode, value == "New worktree" ? value : "Current checkout");
    }

    public string ActiveWorkspacePath => SelectedThread?.WorkspacePath ?? SelectedProjectPath ?? "No workspace selected";

    public string ActiveWorkspaceLabel => SelectedThread?.WorkspaceModeLabel ?? "Current checkout";

    public bool IsTerminalVisible
    {
        get => isTerminalVisible;
        set => SetProperty(ref isTerminalVisible, value);
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
        get => terminalInput;
        set
        {
            if (SetProperty(ref terminalInput, value))
            {
                sendTerminalInputCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TerminalOutput
    {
        get => terminalOutput;
        private set => SetProperty(ref terminalOutput, value);
    }

    public string TerminalStatus
    {
        get => terminalStatus;
        private set => SetProperty(ref terminalStatus, value);
    }

    public string TerminalWorkingDirectory
    {
        get => terminalWorkingDirectory;
        private set => SetProperty(ref terminalWorkingDirectory, value);
    }

    public bool IsTerminalRunning => GetCurrentTerminalState()?.Session.IsRunning == true;

    public string CodexSummary => currentCodex.Summary;

    public string CodexExecutablePath => currentCodex.ExecutablePath ?? "Not found";

    public string CodexVersion => currentCodex.Version ?? "Unknown";

    public string AuthSummary => currentAuth.Summary;

    public string AuthDetail => currentAuth.Detail;

    public string CodexHome => currentAuth.CodexHome ?? "Default not resolved";

    public string SettingsPath => settingsStore.SettingsPath;

    public string PromptText
    {
        get => promptText;
        set => SetProperty(ref promptText, value);
    }

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
        get => selectedThread;
        set
        {
            if (!SetProperty(ref selectedThread, value))
            {
                return;
            }

            SelectThread(value);
            OnPropertyChanged(nameof(ActiveWorkspacePath));
            OnPropertyChanged(nameof(ActiveWorkspaceLabel));
            removeWorktreeCommand.RaiseCanExecuteChanged();
            RefreshVisibleTerminal();
            _ = RefreshGitAsync();
        }
    }

    public string SteeringText
    {
        get => steeringText;
        set
        {
            if (SetProperty(ref steeringText, value))
            {
                steerTurnCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AppServerHealth
    {
        get => appServerHealth;
        private set => SetProperty(ref appServerHealth, value);
    }

    public string FinalResponse =>
        string.IsNullOrWhiteSpace(threadService.FinalResponse)
            ? "No final response yet"
            : threadService.FinalResponse;

    public string GitBranch => gitBranch;

    public string GitStatusMessage
    {
        get => gitStatusMessage;
        private set => SetProperty(ref gitStatusMessage, value);
    }

    public bool IsGitRepository => !string.IsNullOrWhiteSpace(gitRepositoryRoot);

    public GitChangedFile? SelectedGitFile
    {
        get => selectedGitFile;
        set
        {
            if (!SetProperty(ref selectedGitFile, value))
            {
                return;
            }

            RaiseGitCommandStates();
            if (value is null)
            {
                SelectedDiff = "Select a changed file to inspect its diff.";
                return;
            }

            _ = LoadSelectedDiffAsync(value.IsStaged && !value.HasWorkingTreeChanges);
        }
    }

    public string SelectedDiff
    {
        get => selectedDiff;
        private set => SetProperty(ref selectedDiff, value);
    }

    public string DiffViewLabel => isShowingStagedDiff ? "Staged diff" : "Working tree diff";

    public string CommitMessage
    {
        get => commitMessage;
        set
        {
            if (SetProperty(ref commitMessage, value))
            {
                commitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                runCodexDoctorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsGitBusy
    {
        get => isGitBusy;
        private set
        {
            if (SetProperty(ref isGitBusy, value))
            {
                RaiseGitCommandStates();
            }
        }
    }

    public bool IsTurnRunning
    {
        get => isTurnRunning;
        private set
        {
            if (SetProperty(ref isTurnRunning, value))
            {
                if (!value)
                {
                    SteeringText = string.Empty;
                }

                submitPromptCommand.RaiseCanExecuteChanged();
                cancelTurnCommand.RaiseCanExecuteChanged();
                RaiseThreadCommandStates();
            }
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
                RaiseGitCommandStates();
                RaiseTerminalCommandStates();
                toggleTerminalCommand.RaiseCanExecuteChanged();
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
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
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
            await EnsureAppServerClientAsync(cancellationToken).ConfigureAwait(true);
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
        await RefreshGitAsync().ConfigureAwait(true);
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
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            var result = await client.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
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
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            var workspacePath = GetActiveWorkspacePath();
            var result = await client.ResumeThreadAsync(new CodexThreadResumeRequest(
                SelectedThread.ThreadId,
                workspacePath,
                CodexSandbox.WorkspaceWrite,
                NormalizeOverride(ModelOverride))).ConfigureAwait(true);
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
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            var sourceThread = SelectedThread;
            var sourceWorkspace = GetActiveWorkspacePath();
            var result = await client.ForkThreadAsync(new CodexThreadForkRequest(
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
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            await client.ArchiveThreadAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            await StopAndRemoveTerminalAsync(GetTerminalKey(SelectedThread)).ConfigureAwait(true);
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
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            await client.UnarchiveThreadAsync(SelectedThread.ThreadId).ConfigureAwait(true);
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
        if (!CanSteerTurn() || appServerClient is null || activeThreadId is null || activeTurnId is null)
        {
            return;
        }

        try
        {
            await appServerClient.SteerTurnAsync(new CodexTurnSteerRequest(
                activeThreadId,
                activeTurnId,
                SteeringText.Trim())).ConfigureAwait(true);
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
            await StopAndRemoveTerminalAsync(GetTerminalKey(thread)).ConfigureAwait(true);
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
            await RefreshGitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "assistant_worktree_remove_failed", "Could not remove the selected assistant worktree.", exception: ex);
        }
    }

    private async Task StartTerminalAsync()
    {
        if (!CanStartTerminal())
        {
            return;
        }

        var key = GetCurrentTerminalKey();
        try
        {
            if (terminalStates.TryGetValue(key, out var previous))
            {
                await previous.Session.DisposeAsync().ConfigureAwait(true);
                terminalStates.Remove(key);
            }

            var workingDirectory = GetActiveWorkspacePath();
            var session = await terminalService.StartSessionAsync(
                new TerminalStartRequest(workingDirectory, 120, 30)).ConfigureAwait(true);
            var state = new TerminalState(session);
            terminalStates[key] = state;
            session.OutputReceived += (_, args) => HandleTerminalOutput(key, args.Text);
            session.Exited += (_, args) => HandleTerminalExited(key, args.ExitCode);
            IsTerminalVisible = true;
            RefreshVisibleTerminal();
            StatusMessage = "PowerShell terminal started";
            logger.Log(
                AppLogLevel.Information,
                "integrated_terminal_started",
                "An integrated terminal session was started.",
                new Dictionary<string, string?> { ["key"] = key, ["workingDirectory"] = workingDirectory });
        }
        catch (Exception ex)
        {
            TerminalStatus = "Start failed";
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "integrated_terminal_start_failed", "Could not start an integrated terminal.", exception: ex);
        }
        finally
        {
            RaiseTerminalCommandStates();
        }
    }

    private async Task SendTerminalInputAsync()
    {
        var state = GetCurrentTerminalState();
        if (state?.Session.IsRunning != true || string.IsNullOrWhiteSpace(TerminalInput))
        {
            return;
        }

        var input = TerminalInput;
        try
        {
            await state.Session.WriteInputAsync(input + "\r\n").ConfigureAwait(true);
            TerminalInput = string.Empty;
            StatusMessage = "Terminal input sent";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "integrated_terminal_input_failed", "Could not write to the integrated terminal.", exception: ex);
        }
    }

    private void ClearTerminal()
    {
        var state = GetCurrentTerminalState();
        if (state is null)
        {
            return;
        }

        state.Output.Clear();
        TerminalOutput = string.Empty;
        clearTerminalCommand.RaiseCanExecuteChanged();
        StatusMessage = "Terminal output cleared";
    }

    private async Task KillTerminalAsync()
    {
        var state = GetCurrentTerminalState();
        if (state?.Session.IsRunning != true)
        {
            return;
        }

        try
        {
            await state.Session.StopAsync().ConfigureAwait(true);
            TerminalStatus = "Exited";
            StatusMessage = "Terminal session stopped";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "integrated_terminal_stop_failed", "Could not stop the integrated terminal.", exception: ex);
        }
        finally
        {
            RaiseTerminalCommandStates();
        }
    }

    private void ToggleTerminal()
    {
        IsTerminalVisible = !IsTerminalVisible;
        if (IsTerminalVisible)
        {
            SelectedWorkspaceTabIndex = 1;
        }

        StatusMessage = IsTerminalVisible ? "Terminal panel shown" : "Terminal panel hidden";
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

    private void HandleTerminalOutput(string key, string text)
    {
        void Apply()
        {
            if (!terminalStates.TryGetValue(key, out var state))
            {
                return;
            }

            state.Output.Append(text);
            const int maximumOutputCharacters = 250_000;
            if (state.Output.Length > maximumOutputCharacters)
            {
                state.Output.Remove(0, state.Output.Length - maximumOutputCharacters);
            }

            if (string.Equals(key, GetCurrentTerminalKeyOrNull(), StringComparison.OrdinalIgnoreCase))
            {
                TerminalOutput = state.Output.ToString();
                clearTerminalCommand.RaiseCanExecuteChanged();
            }
        }

        if (synchronizationContext is null)
        {
            Apply();
        }
        else
        {
            synchronizationContext.Post(_ => Apply(), null);
        }
    }

    private void HandleTerminalExited(string key, int exitCode)
    {
        void Apply()
        {
            if (!terminalStates.TryGetValue(key, out var state))
            {
                return;
            }

            state.ExitCode = exitCode;
            if (string.Equals(key, GetCurrentTerminalKeyOrNull(), StringComparison.OrdinalIgnoreCase))
            {
                TerminalStatus = $"Exited ({exitCode})";
                OnPropertyChanged(nameof(IsTerminalRunning));
                RaiseTerminalCommandStates();
            }
        }

        if (synchronizationContext is null)
        {
            Apply();
        }
        else
        {
            synchronizationContext.Post(_ => Apply(), null);
        }
    }

    private void RefreshVisibleTerminal()
    {
        var state = GetCurrentTerminalState();
        TerminalOutput = state?.Output.ToString() ?? string.Empty;
        TerminalWorkingDirectory = state?.Session.WorkingDirectory
            ?? (GetActiveWorkspacePathIfAvailable() ?? "No terminal session");
        TerminalStatus = state is null
            ? "Not started"
            : state.Session.IsRunning ? "Running" : $"Exited ({state.ExitCode ?? 0})";
        OnPropertyChanged(nameof(IsTerminalRunning));
        RaiseTerminalCommandStates();
    }

    private string? GetActiveWorkspacePathIfAvailable()
    {
        var path = SelectedThread?.WorkspacePath ?? SelectedProjectPath;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private string GetCurrentTerminalKey() => GetCurrentTerminalKeyOrNull()
        ?? throw new InvalidOperationException("Select a project before starting a terminal.");

    private string? GetCurrentTerminalKeyOrNull()
    {
        if (!string.IsNullOrWhiteSpace(SelectedThread?.ThreadId))
        {
            return SelectedThread.ThreadId;
        }

        return string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? null
            : $"project:{Path.GetFullPath(SelectedProjectPath)}";
    }

    private static string GetTerminalKey(ProjectThreadState thread) => thread.ThreadId;

    private TerminalState? GetCurrentTerminalState()
    {
        var key = GetCurrentTerminalKeyOrNull();
        return key is not null && terminalStates.TryGetValue(key, out var state) ? state : null;
    }

    private async Task StopAndRemoveTerminalAsync(string key)
    {
        if (!terminalStates.Remove(key, out var state))
        {
            return;
        }

        await state.Session.DisposeAsync().ConfigureAwait(true);
        RefreshVisibleTerminal();
    }

    private async Task DisposeTerminalSessionsAsync()
    {
        var states = terminalStates.Values.ToArray();
        terminalStates.Clear();
        foreach (var state in states)
        {
            try
            {
                await state.Session.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                logger.Log(AppLogLevel.Warning, "integrated_terminal_dispose_failed", "Could not dispose an integrated terminal session.", exception: ex);
            }
        }

        RefreshVisibleTerminal();
    }

    private async Task RefreshDiagnosticsAsync()
    {
        IsBusy = true;
        StatusMessage = "Refreshing diagnostics";

        try
        {
            currentCodex = await codexDiscoveryService.DetectAsync(settings.PreferredCodexPath).ConfigureAwait(true);
            currentAuth = await authService.GetAuthenticationStateAsync(currentCodex).ConfigureAwait(true);
            RefreshDiagnosticLines();
            RaiseComputedProperties();
            StatusMessage = "Diagnostics refreshed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunCodexDoctorAsync()
    {
        if (!CanRunCodexDoctor())
        {
            StatusMessage = "Codex doctor is unavailable";
            return;
        }

        IsBusy = true;
        StatusMessage = "Running Codex doctor";
        try
        {
            lastDoctorResult = await codexCliUtilityRunner.RunDoctorAsync(currentCodex).ConfigureAwait(true);
            RefreshDiagnosticLines();
            StatusMessage = lastDoctorResult.Succeeded
                ? "Codex doctor completed"
                : $"Codex doctor failed with exit code {lastDoctorResult.ExitCode}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_doctor_failed", "Could not run codex doctor.", exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunCodexDoctor() => !IsShuttingDown && !IsBusy && currentCodex.IsFound;

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

    private async Task RefreshGitAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            ResetGitState("Select a project to inspect Git changes");
            return;
        }

        var previousPath = SelectedGitFile?.Path;
        IsGitBusy = true;
        GitStatusMessage = "Refreshing Git status";
        try
        {
            var workspacePath = SelectedThread?.WorkspacePath ?? SelectedProjectPath;
            if (!Directory.Exists(workspacePath))
            {
                ResetGitState($"The active workspace is unavailable: {workspacePath}");
                return;
            }

            var state = await gitService.GetRepositoryStateAsync(workspacePath).ConfigureAwait(true);
            ChangedFiles.Clear();
            gitRepositoryRoot = state.RootPath;
            gitBranch = state.Branch ?? "No repository";
            OnPropertyChanged(nameof(GitBranch));
            OnPropertyChanged(nameof(IsGitRepository));

            if (!state.IsRepository)
            {
                SelectedGitFile = null;
                GitStatusMessage = state.ErrorMessage ?? "No Git repository detected";
                return;
            }

            foreach (var file in state.ChangedFiles)
            {
                ChangedFiles.Add(file);
            }

            SelectedGitFile = ChangedFiles.FirstOrDefault(file =>
                string.Equals(file.Path, previousPath, StringComparison.OrdinalIgnoreCase));
            GitStatusMessage = ChangedFiles.Count == 0
                ? $"{GitBranch}: working tree clean"
                : $"{GitBranch}: {ChangedFiles.Count} changed file{(ChangedFiles.Count == 1 ? string.Empty : "s")}";
        }
        catch (Exception ex)
        {
            ResetGitState(ex.Message);
            logger.Log(AppLogLevel.Warning, "git_status_failed", "Could not refresh Git status.", exception: ex);
        }
        finally
        {
            IsGitBusy = false;
            RaiseGitCommandStates();
        }
    }

    private async Task LoadSelectedDiffAsync(bool staged)
    {
        if (SelectedGitFile is null || string.IsNullOrWhiteSpace(gitRepositoryRoot))
        {
            return;
        }

        IsGitBusy = true;
        isShowingStagedDiff = staged;
        OnPropertyChanged(nameof(DiffViewLabel));
        SelectedDiff = "Loading diff...";
        try
        {
            SelectedDiff = await gitService.GetDiffAsync(
                gitRepositoryRoot,
                SelectedGitFile,
                staged).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SelectedDiff = ex.Message;
            GitStatusMessage = "Could not load the selected diff";
            logger.Log(AppLogLevel.Warning, "git_diff_failed", "Could not load a Git diff.", exception: ex);
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    private async Task StageSelectedFileAsync()
    {
        if (SelectedGitFile is null || string.IsNullOrWhiteSpace(gitRepositoryRoot))
        {
            return;
        }

        var path = SelectedGitFile.Path;
        await RunGitMutationAsync(
            () => gitService.StageAsync(gitRepositoryRoot, [path]),
            $"Staged {path}").ConfigureAwait(true);
    }

    private async Task UnstageSelectedFileAsync()
    {
        if (SelectedGitFile is null || string.IsNullOrWhiteSpace(gitRepositoryRoot))
        {
            return;
        }

        var path = SelectedGitFile.Path;
        await RunGitMutationAsync(
            () => gitService.UnstageAsync(gitRepositoryRoot, [path]),
            $"Unstaged {path}").ConfigureAwait(true);
    }

    private async Task RevertSelectedFileAsync()
    {
        if (SelectedGitFile is null || string.IsNullOrWhiteSpace(gitRepositoryRoot))
        {
            return;
        }

        var file = SelectedGitFile;
        var action = file.IsUntracked ? "delete the untracked file" : "discard its staged and working-tree changes";
        var confirmed = userInteractionService.ConfirmDestructiveAction(
            "Discard Git changes",
            $"This will {action}:\n\n{file.DisplayPath}\n\nThis cannot be undone. Continue?");
        if (!confirmed)
        {
            GitStatusMessage = "Discard cancelled";
            return;
        }

        await RunGitMutationAsync(
            () => gitService.RevertAsync(gitRepositoryRoot, [file]),
            $"Discarded changes to {file.Path}").ConfigureAwait(true);
    }

    private async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(gitRepositoryRoot))
        {
            return;
        }

        IsGitBusy = true;
        try
        {
            var result = await gitService.CommitAsync(gitRepositoryRoot, CommitMessage).ConfigureAwait(true);
            CommitMessage = string.Empty;
            await RefreshGitAsync().ConfigureAwait(true);
            GitStatusMessage = $"Committed {result.CommitId}: {result.Summary}";
            StatusMessage = $"Git commit {result.CommitId} created";
        }
        catch (Exception ex)
        {
            GitStatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "git_commit_failed", "Could not create a Git commit.", exception: ex);
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    private async Task RunGitMutationAsync(Func<Task> operation, string successMessage)
    {
        IsGitBusy = true;
        try
        {
            await operation().ConfigureAwait(true);
            await RefreshGitAsync().ConfigureAwait(true);
            GitStatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            GitStatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "git_mutation_failed", "A Git operation failed.", exception: ex);
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    private void OpenInEditor()
    {
        try
        {
            userInteractionService.OpenInEditor(GetSelectedProjectTargetPath());
            GitStatusMessage = "Opened in editor";
        }
        catch (Exception ex)
        {
            GitStatusMessage = ex.Message;
        }
    }

    private void RevealInExplorer()
    {
        try
        {
            userInteractionService.RevealInExplorer(GetSelectedProjectTargetPath());
            GitStatusMessage = "Opened in Explorer";
        }
        catch (Exception ex)
        {
            GitStatusMessage = ex.Message;
        }
    }

    private string GetSelectedProjectTargetPath()
    {
        var root = gitRepositoryRoot ?? SelectedProjectPath
            ?? throw new InvalidOperationException("Select a project first.");
        return SelectedGitFile is null
            ? root
            : Path.GetFullPath(Path.Combine(root, SelectedGitFile.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private void ResetGitState(string message)
    {
        gitRepositoryRoot = null;
        gitBranch = "No repository";
        ChangedFiles.Clear();
        SelectedGitFile = null;
        GitStatusMessage = message;
        OnPropertyChanged(nameof(GitBranch));
        OnPropertyChanged(nameof(IsGitRepository));
        RaiseGitCommandStates();
    }

    private async Task StartLoginAsync(LoginMethod method)
    {
        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before signing in";
            return;
        }

        var started = await authService.StartLoginAsync(currentCodex, method).ConfigureAwait(true);
        StatusMessage = started ? "Sign-in opened in a terminal window" : "Could not start sign-in";
    }

    private async Task SignOutAsync()
    {
        if (!currentCodex.IsFound)
        {
            StatusMessage = "Codex CLI is not available";
            return;
        }

        var started = await authService.StartLogoutAsync(currentCodex).ConfigureAwait(true);
        StatusMessage = started ? "Sign-out opened in a terminal window" : "Could not start sign-out";
    }

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

        IsTurnRunning = true;
        StatusMessage = "Starting Codex task";

        try
        {
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            activeThreadId = await EnsureActiveThreadAsync(client).ConfigureAwait(true);
            runningThreadIds.Add(activeThreadId);
            UpdateThreadActivity(activeThreadId, isRunning: true, "Running");
            IsTurnRunning = true;

            var submittedPrompt = PromptText.Trim();
            var workspacePath = GetActiveWorkspacePath();
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            var turn = await client.StartTurnAsync(new CodexTurnStartRequest(
                activeThreadId,
                submittedPrompt,
                workspacePath,
                CodexSandbox.WorkspaceWrite,
                NormalizeOverride(ModelOverride),
                ParseReasoningEffort(ReasoningEffortOverride))).ConfigureAwait(true);

            activeTurnId = turn.TurnId;
            activeTurnIds[activeThreadId] = turn.TurnId;
            threadWorkspace.RegisterTurn(activeThreadId, turn.TurnId);
            cancelTurnCommand.RaiseCanExecuteChanged();
            StatusMessage = "Codex turn running";
        }
        catch (Exception ex)
        {
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

    private async Task<string> EnsureActiveThreadAsync(CodexAppServerClient client)
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            throw new InvalidOperationException("A project must be selected before starting a Codex thread.");
        }

        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
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
            var resumed = await client.ResumeThreadAsync(new CodexThreadResumeRequest(
                activeThreadId,
                GetActiveWorkspacePath(),
                CodexSandbox.WorkspaceWrite)).ConfigureAwait(true);
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

            var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
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

        if (!CanCancelTurn() || appServerClient is null || string.IsNullOrWhiteSpace(activeThreadId) || string.IsNullOrWhiteSpace(activeTurnId))
        {
            StatusMessage = "No active turn to cancel";
            return;
        }

        try
        {
            await appServerClient.CancelTurnAsync(activeThreadId, activeTurnId).ConfigureAwait(true);
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
        await DisposeTerminalSessionsAsync().ConfigureAwait(true);
        await SaveActiveThreadStateAsync().ConfigureAwait(true);
        await DisposeAppServerClientAsync().ConfigureAwait(true);

        IsTurnRunning = false;
        activeTurnId = null;
        activeTurnIds.Clear();
        runningThreadIds.Clear();
        StatusMessage = "Application closed";
    }

    private async Task TryCancelRunningTurnForShutdownAsync(CancellationToken cancellationToken)
    {
        if (appServerClient is null)
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
                await appServerClient.CancelTurnAsync(turn.Key, turn.Value, timeout.Token).ConfigureAwait(true);
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
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            var models = await client.ListModelsAsync().ConfigureAwait(true);
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
            appServerClient is not null &&
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
        appServerClient is not null &&
        !string.IsNullOrWhiteSpace(activeThreadId) &&
        !string.IsNullOrWhiteSpace(activeTurnId) &&
        !string.IsNullOrWhiteSpace(SteeringText);

    private bool CanStartTerminal()
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            return false;
        }

        var workspace = GetActiveWorkspacePathIfAvailable();
        return !string.IsNullOrWhiteSpace(workspace) &&
            Directory.Exists(workspace) &&
            GetCurrentTerminalState()?.Session.IsRunning != true;
    }

    private bool CanSendTerminalInput() =>
        !IsShuttingDown &&
        GetCurrentTerminalState()?.Session.IsRunning == true &&
        !string.IsNullOrWhiteSpace(TerminalInput);

    private bool CanKillTerminal() =>
        !IsShuttingDown && GetCurrentTerminalState()?.Session.IsRunning == true;

    private bool CanClearTerminal() => GetCurrentTerminalState()?.Output.Length > 0;

    private void RaiseTerminalCommandStates()
    {
        startTerminalCommand.RaiseCanExecuteChanged();
        sendTerminalInputCommand.RaiseCanExecuteChanged();
        killTerminalCommand.RaiseCanExecuteChanged();
        clearTerminalCommand.RaiseCanExecuteChanged();
    }

    private void RaiseThreadCommandStates()
    {
        newThreadCommand.RaiseCanExecuteChanged();
        resumeThreadCommand.RaiseCanExecuteChanged();
        forkThreadCommand.RaiseCanExecuteChanged();
        archiveThreadCommand.RaiseCanExecuteChanged();
        unarchiveThreadCommand.RaiseCanExecuteChanged();
        steerTurnCommand.RaiseCanExecuteChanged();
        removeWorktreeCommand.RaiseCanExecuteChanged();
    }

    private bool CanUseGitProject() =>
        !IsShuttingDown && !IsGitBusy && !string.IsNullOrWhiteSpace(SelectedProjectPath);

    private bool CanShowWorkingDiff() =>
        CanMutateSelectedFile() && SelectedGitFile?.HasWorkingTreeChanges == true;

    private bool CanShowStagedDiff() =>
        CanMutateSelectedFile() && SelectedGitFile?.IsStaged == true;

    private bool CanStageSelectedFile() =>
        CanMutateSelectedFile() && SelectedGitFile?.HasWorkingTreeChanges == true;

    private bool CanUnstageSelectedFile() =>
        CanMutateSelectedFile() && SelectedGitFile?.IsStaged == true;

    private bool CanMutateSelectedFile() =>
        !IsShuttingDown && !IsGitBusy && IsGitRepository && SelectedGitFile is not null;

    private bool CanCommit() =>
        !IsShuttingDown &&
        !IsGitBusy &&
        IsGitRepository &&
        !string.IsNullOrWhiteSpace(CommitMessage) &&
        ChangedFiles.Any(file => file.IsStaged);

    private bool CanOpenProjectTarget() =>
        !IsShuttingDown && !string.IsNullOrWhiteSpace(SelectedProjectPath);

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

    private void RaiseGitCommandStates()
    {
        refreshGitCommand.RaiseCanExecuteChanged();
        showWorkingDiffCommand.RaiseCanExecuteChanged();
        showStagedDiffCommand.RaiseCanExecuteChanged();
        stageSelectedFileCommand.RaiseCanExecuteChanged();
        unstageSelectedFileCommand.RaiseCanExecuteChanged();
        revertSelectedFileCommand.RaiseCanExecuteChanged();
        commitCommand.RaiseCanExecuteChanged();
        openInEditorCommand.RaiseCanExecuteChanged();
        revealInExplorerCommand.RaiseCanExecuteChanged();
    }

    private async Task<CodexAppServerClient> EnsureAppServerClientAsync(CancellationToken cancellationToken = default)
    {
        if (appServerClient?.IsHealthy == true)
        {
            return appServerClient;
        }

        await appServerLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (appServerClient?.IsHealthy == true)
            {
                return appServerClient;
            }

            var isRecovery = appServerClient is not null || string.Equals(AppServerHealth, "Codex reconnecting", StringComparison.Ordinal);
            if (appServerClient is not null)
            {
                await DisposeAppServerClientAsync().ConfigureAwait(true);
            }

            AppServerHealth = isRecovery ? "Codex reconnecting" : "Codex connecting";

            CodexAppServerClient? client = null;
            try
            {
                var transport = await codexProcessService
                    .StartAppServerTransportAsync(currentCodex, cancellationToken)
                    .ConfigureAwait(true);
                client = new CodexAppServerClient(
                    transport,
                    new CodexAppServerClientMetadata("native_codex_assistant", "Native Codex Assistant", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0"));
                client.NotificationReceived += OnAppServerNotificationReceived;
                client.ConnectionFailed += OnAppServerConnectionFailed;
                await client.InitializeAsync(CodexInitializeOptions.Default, cancellationToken).ConfigureAwait(true);
                appServerClient = client;
                AppServerHealth = "Codex connected";
                return appServerClient;
            }
            catch
            {
                if (client is not null)
                {
                    client.NotificationReceived -= OnAppServerNotificationReceived;
                    client.ConnectionFailed -= OnAppServerConnectionFailed;
                    await client.DisposeAsync().ConfigureAwait(true);
                }

                AppServerHealth = "Codex unavailable";
                throw;
            }
        }
        finally
        {
            appServerLifecycleGate.Release();
        }
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
        if (synchronizationContext is null)
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
            _ = RefreshGitAsync();
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
        persisted.UpdatedAt = DateTimeOffset.UtcNow;
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
        RecentProjects.Clear();
        foreach (var project in settings.RecentProjects)
        {
            RecentProjects.Add(project);
        }
    }

    private void RefreshDiagnosticLines()
    {
        Diagnostics.Clear();
        Diagnostics.Add($"App: {Assembly.GetExecutingAssembly().GetName().Version}");
        Diagnostics.Add($"Windows: {Environment.OSVersion.VersionString}");
        Diagnostics.Add($".NET: {Environment.Version}");
        Diagnostics.Add($"Codex: {CodexSummary}");
        Diagnostics.Add($"Codex path: {CodexExecutablePath}");
        Diagnostics.Add($"Codex version: {CodexVersion}");
        Diagnostics.Add($"Sign-in: {AuthSummary}");
        Diagnostics.Add($"Codex home: {CodexHome}");
        Diagnostics.Add($"Settings: {SettingsPath}");
        if (lastDoctorResult is not null)
        {
            Diagnostics.Add($"Codex doctor exit code: {lastDoctorResult.ExitCode}");
            foreach (var line in SplitDiagnosticLines(lastDoctorResult.StandardOutput))
            {
                Diagnostics.Add($"Doctor: {line}");
            }

            foreach (var line in SplitDiagnosticLines(lastDoctorResult.StandardError))
            {
                Diagnostics.Add($"Doctor stderr: {line}");
            }
        }
    }

    private static IEnumerable<string> SplitDiagnosticLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(CodexSummary));
        OnPropertyChanged(nameof(CodexExecutablePath));
        OnPropertyChanged(nameof(CodexVersion));
        OnPropertyChanged(nameof(AuthSummary));
        OnPropertyChanged(nameof(AuthDetail));
        OnPropertyChanged(nameof(CodexHome));
        OnPropertyChanged(nameof(SettingsPath));
        runCodexDoctorCommand.RaiseCanExecuteChanged();
        RaiseThreadCommandStates();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task DisposeAppServerClientAsync()
    {
        if (appServerClient is null)
        {
            return;
        }

        appServerClient.NotificationReceived -= OnAppServerNotificationReceived;
        appServerClient.ConnectionFailed -= OnAppServerConnectionFailed;
        await appServerClient.DisposeAsync().ConfigureAwait(false);
        appServerClient = null;
    }

    private sealed class TerminalState(ITerminalSession session)
    {
        public ITerminalSession Session { get; } = session;

        public System.Text.StringBuilder Output { get; } = new();

        public int? ExitCode { get; set; }
    }
}
