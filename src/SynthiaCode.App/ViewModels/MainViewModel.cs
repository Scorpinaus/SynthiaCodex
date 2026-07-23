using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Core.Workspaces;
using SynthiaCode.Infrastructure.Attachments;

namespace SynthiaCode.App.ViewModels;

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
    private readonly CodexFollowUpQueueWorkspace followUpQueueWorkspace = new();
    private readonly IAppLogger logger;
    private readonly IGeneralWorkspaceService generalWorkspaceService;
    private readonly IAttachmentStore? attachmentStore;
    private readonly WorkspaceAttachmentResolver workspaceAttachmentResolver;
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
    private readonly AsyncRelayCommand togglePinThreadCommand;
    private readonly AsyncRelayCommand deleteThreadCommand;
    private readonly AsyncRelayCommand steerTurnCommand;
    private readonly AsyncRelayCommand removeWorktreeCommand;
    private readonly RelayCommand toggleProjectRailCommand;
    private readonly RelayCommand toggleDetailsPaneCommand;
    private readonly RelayCommand openSettingsCommand;

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
    private bool executionPolicyLoaded;
    private string? executionPolicyCwd;
    private bool isShuttingDown;
    private bool isRestoringAttachmentDraft;
    private string? generalWorkspacePath;
    private string? generalWorkspaceError;
    private Task? shutdownTask;
    private Task? appServerWarmUpTask;
    private readonly HashSet<string> loadedThreadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> runningThreadIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeTurnIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> followUpDispatchGates = new(StringComparer.Ordinal);

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
        IAppLogger logger,
        IGeneralWorkspaceService generalWorkspaceService,
        IAttachmentStore? attachmentStore = null,
        WorkspaceAttachmentResolver? workspaceAttachmentResolver = null)
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
        this.generalWorkspaceService = generalWorkspaceService;
        this.attachmentStore = attachmentStore;
        this.workspaceAttachmentResolver = workspaceAttachmentResolver ?? new WorkspaceAttachmentResolver();
        synchronizationContext = SynchronizationContext.Current;
        appServerSessionCoordinator.NotificationReceived += OnAppServerNotificationReceived;
        appServerSessionCoordinator.ServerRequestReceived += OnServerRequestReceived;
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
            userInteractionService.OpenExternalUri,
            SendAlternateFollowUpAsync,
            PersistSelectedFollowUpQueueAsync,
            SendQueuedFollowUpNowAsync,
            EditPromptAsync);
        TaskWorkspace.PropertyChanged += (_, args) => RelayTaskPropertyChanged(args.PropertyName);

        ApprovalQueue = new ApprovalQueueViewModel(appServerSessionCoordinator.RespondToServerRequestAsync);
        ExecutionPolicy = new ExecutionPolicyViewModel(
            userInteractionService.ConfirmDestructiveAction,
            OnExecutionPolicyChanged);

        ProjectWorkspace = new ProjectThreadViewModel(
            BrowseProjectAsync,
            OpenRecentProjectAsync,
            NewThreadForCurrentScopeAsync,
            NewGeneralThreadAsync,
            NewProjectThreadAsync,
            ResumeSelectedThreadAsync,
            ForkSelectedThreadAsync,
            ArchiveSelectedThreadAsync,
            UnarchiveSelectedThreadAsync,
            RemoveSelectedWorktreeAsync,
            CanCreateThreadInCurrentScope,
            CanCreateGeneralThread,
            CanUseSelectedThread,
            CanArchiveSelectedThread,
            CanUnarchiveSelectedThread,
            CanRemoveSelectedWorktree,
            HandleSelectedThreadChanged,
            ToggleSelectedThreadPinAsync,
            DeleteSelectedThreadAsync,
            CanToggleSelectedThreadPin,
            CanDeleteSelectedThread);
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
        OpenSettingsCommand = openSettingsCommand = new RelayCommand(OpenSettings, () => !IsShuttingDown);
        Account = new AccountViewModel(
            cancellationToken => appServerSessionCoordinator.ReadAccountAsync(false, cancellationToken),
            appServerSessionCoordinator.ReadAccountRateLimitsAsync,
            OpenSettings,
            SignInChatGptCommand,
            SignOutCommand,
            logger);
    }

    public event EventHandler? CloseRequested;

    public async Task AddImageFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (attachmentStore is null)
        {
            throw new InvalidOperationException("Attachment storage is unavailable.");
        }

        var imported = 0;
        var failures = new List<string>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var attachment = await attachmentStore.ImportFileAsync(path, cancellationToken).ConfigureAwait(true);
                TaskWorkspace.AddAttachment(attachment);
                imported++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        StatusMessage = failures.Count == 0
            ? $"Added {imported} image{(imported == 1 ? string.Empty : "s")}"
            : imported == 0
                ? failures[0]
                : $"Added {imported} image{(imported == 1 ? string.Empty : "s")}; {failures.Count} skipped";
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
        var added = 0;
        var failures = new List<string>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AttachmentReference attachment;
                var isWithinWorkspace = workspaceAttachmentResolver.IsWithinWorkspace(workspacePath, path);
                if (Directory.Exists(path))
                {
                    if (isWithinWorkspace)
                    {
                        attachment = workspaceAttachmentResolver.Resolve(workspacePath, path, AttachmentKind.Folder);
                    }
                    else
                    {
                        if (attachmentStore is null)
                        {
                            throw new InvalidOperationException("Attachment storage is unavailable.");
                        }
                        attachment = await attachmentStore.ImportFolderAsync(path, cancellationToken).ConfigureAwait(true);
                    }
                }
                else if (IsSupportedImagePath(path))
                {
                    if (attachmentStore is null)
                    {
                        throw new InvalidOperationException("Attachment storage is unavailable.");
                    }
                    attachment = await attachmentStore.ImportFileAsync(path, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    if (isWithinWorkspace)
                    {
                        attachment = workspaceAttachmentResolver.Resolve(workspacePath, path, AttachmentKind.File);
                    }
                    else
                    {
                        if (attachmentStore is null)
                        {
                            throw new InvalidOperationException("Attachment storage is unavailable.");
                        }
                        attachment = await attachmentStore.ImportExternalFileAsync(path, cancellationToken).ConfigureAwait(true);
                    }
                }

                TaskWorkspace.AddAttachment(attachment);
                added++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        StatusMessage = failures.Count == 0
            ? $"Added {added} attachment{(added == 1 ? string.Empty : "s")}"
            : added == 0
                ? failures[0]
                : $"Added {added} attachment{(added == 1 ? string.Empty : "s")}; {failures.Count} skipped";
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
        if (attachmentStore is null)
        {
            throw new InvalidOperationException("Attachment storage is unavailable.");
        }

        var attachment = await attachmentStore
            .ImportStreamAsync(imageStream, displayName, cancellationToken)
            .ConfigureAwait(true);
        TaskWorkspace.AddAttachment(attachment);
        StatusMessage = "Added pasted image";
    }

    public void ReportAttachmentError(string message) =>
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Could not add the attachment." : message;

    public void OpenAttachment(AttachmentReference attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var path = attachment.SourceKind == AttachmentSourceKind.ManagedCopy
            ? attachmentStore?.ResolvePath(attachment) ?? attachment.ManagedPath
            : workspaceAttachmentResolver.Revalidate(GetActiveWorkspacePath(), attachment).ManagedPath;
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
        if (isRestoringAttachmentDraft)
        {
            return;
        }

        var scope = string.IsNullOrWhiteSpace(projectPath)
            ? ThreadScopeKey.General
            : ThreadScopeKey.ForProject(projectPath);
        var draft = settings.ComposerAttachmentDrafts.FirstOrDefault(item =>
            scope.Matches(item.ScopeKind, item.ProjectPath) &&
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));
        if (TaskWorkspace.Attachments.Count == 0)
        {
            if (draft is not null)
            {
                settings.ComposerAttachmentDrafts.Remove(draft);
            }
            return;
        }

        draft ??= new ComposerAttachmentDraftSnapshot
        {
            ScopeKind = scope.Kind,
            ProjectPath = scope.ProjectPath ?? string.Empty,
            ThreadId = threadId
        };
        if (!settings.ComposerAttachmentDrafts.Contains(draft))
        {
            settings.ComposerAttachmentDrafts.Add(draft);
        }
        draft.Attachments = [.. TaskWorkspace.Attachments.Select(attachment => attachment.Clone())];
        draft.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void RestoreAttachmentDraft(string? projectPath, string? threadId)
    {
        isRestoringAttachmentDraft = true;
        try
        {
            var scope = string.IsNullOrWhiteSpace(projectPath)
                ? ThreadScopeKey.General
                : ThreadScopeKey.ForProject(projectPath);
            var draft = settings.ComposerAttachmentDrafts.FirstOrDefault(item =>
                scope.Matches(item.ScopeKind, item.ProjectPath) &&
                string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));
            var attachments = (draft?.Attachments ?? []).Select(attachment =>
            {
                if (attachment.SourceKind != AttachmentSourceKind.WorkspaceReference)
                {
                    return attachment;
                }
                try
                {
                    return workspaceAttachmentResolver.Revalidate(GetActiveWorkspacePath(), attachment);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    attachment.ManagedPath = null;
                    return attachment;
                }
            });
            TaskWorkspace.ReplaceAttachments(attachments);
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

    public ICommand OpenSettingsCommand { get; }

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
                openSettingsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        logger.Log(AppLogLevel.Information, "view_model_initialize", "Main view model initialization started.");
        settings = await settingsStore.LoadAsync().ConfigureAwait(true);
        try
        {
            generalWorkspacePath = generalWorkspaceService.EnsureWorkspace();
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
        if (attachmentStore is null)
        {
            return;
        }

        var references = settings.ProjectThreads
            .SelectMany(thread =>
                thread.ConversationTurns.SelectMany(turn => turn.UserAttachments)
                    .Concat(thread.QueuedFollowUps.SelectMany(item => item.Attachments)))
            .Concat(settings.ComposerAttachmentDrafts.SelectMany(draft => draft.Attachments))
            .ToList();
        foreach (var attachment in references.Where(item => item.SourceKind == AttachmentSourceKind.ManagedCopy))
        {
            try
            {
                attachment.ManagedPath = attachmentStore.ResolvePath(attachment);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                attachment.ManagedPath = null;
                logger.Log(
                    AppLogLevel.Warning,
                    "attachment_restore_unavailable",
                    "A persisted managed attachment is unavailable.",
                    new Dictionary<string, string?> { ["storageKey"] = attachment.StorageKey },
                    ex);
            }
        }

        try
        {
            await attachmentStore.CleanupAsync(references
                .Where(item => item.SourceKind == AttachmentSourceKind.ManagedCopy)
                .Select(item => item.StorageKey)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "attachment_cleanup_failed",
                "Managed attachment cleanup could not be completed.",
                exception: ex);
        }
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
        CaptureAttachmentDraft(SelectedProjectPath, activeThreadId);
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
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var result = await appServerSessionCoordinator
                .StartThreadAsync(CreateThreadStartOptions(workspacePath))
                .ConfigureAwait(true);
            AssistantWorktree? worktree = null;
            if (scope.Kind == ThreadScopeKind.Project &&
                string.Equals(NewThreadWorkspaceMode, "New worktree", StringComparison.Ordinal))
            {
                var repository = await gitService.GetRepositoryStateAsync(scope.ProjectPath!).ConfigureAwait(true);
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
                scope,
                result.ThreadId,
                $"Thread {ProjectThreads.Count + 1}",
                worktree?.Path ?? workspacePath,
                worktree?.Branch);
            loadedThreadIds.Add(result.ThreadId);
            RefreshProjectThreads(result.ThreadId);
            StatusMessage = scope.Kind == ThreadScopeKind.General
                ? "New Codex thread created in General"
                : worktree is null
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
        if (!CanUseSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var workspacePath = GetActiveWorkspacePath();
            var result = await appServerSessionCoordinator
                .ResumeThreadAsync(CreateThreadResumeRequest(SelectedThread.ThreadId, workspacePath))
                .ConfigureAwait(true);
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
        if (!CanUseSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var sourceThread = SelectedThread;
            var sourceWorkspace = GetActiveWorkspacePath();
            var result = await appServerSessionCoordinator
                .ForkThreadAsync(CreateThreadForkRequest(sourceThread.ThreadId, sourceWorkspace))
                .ConfigureAwait(true);
            AssistantWorktree? worktree = null;
            if (sourceThread.ScopeKind == ThreadScopeKind.Project &&
                string.Equals(sourceThread.Mode, "worktree", StringComparison.OrdinalIgnoreCase))
            {
                var repository = await gitService.GetRepositoryStateAsync(sourceThread.ProjectPath).ConfigureAwait(true);
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
                sourceThread.ScopeKey,
                result.ThreadId,
                $"Fork of {sourceThread.DisplayTitle}",
                worktree?.Path ?? sourceWorkspace,
                worktree?.Branch);
            state.Preview = sourceThread.Preview;
            var sourceService = threadWorkspace.GetRequired(sourceThread.ThreadId);
            state.FinalResponse = sourceService.FinalResponse;
            state.ConversationTurns = sourceService.SnapshotConversation().Select(CloneConversationTurn).ToList();
            state.ContextTokensUsed = sourceService.ContextTokensUsed;
            state.ContextWindowTokens = sourceService.ContextWindowTokens;
            state.ContextCompactionCount = sourceService.ContextCompactionCount;
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
        if (!CanArchiveSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            await appServerSessionCoordinator.ArchiveThreadAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            await Terminal.StopAndRemoveAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            threadStore.SetArchived(settings, SelectedThread.ThreadId, archived: true);
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
        if (!CanUnarchiveSelectedThread() || SelectedThread is null)
        {
            return;
        }

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            await appServerSessionCoordinator.UnarchiveThreadAsync(SelectedThread.ThreadId).ConfigureAwait(true);
            threadStore.SetArchived(settings, SelectedThread.ThreadId, archived: false);
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
            threadStore.SetPinned(settings, threadId, pinned);
            RefreshProjectThreads(threadId);
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
            StatusMessage = pinned ? "Chat pinned" : "Chat unpinned";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "thread_pin_failed", "Could not update the selected chat pin.", exception: ex);
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
                await EnsureAppServerSessionAsync().ConfigureAwait(true);
                await appServerSessionCoordinator.ArchiveThreadAsync(thread.ThreadId).ConfigureAwait(true);
            }

            await Terminal.StopAndRemoveAsync(thread.ThreadId).ConfigureAwait(true);
            loadedThreadIds.Remove(thread.ThreadId);
            runningThreadIds.Remove(thread.ThreadId);
            activeTurnIds.Remove(thread.ThreadId);
            threadWorkspace.Remove(thread.ThreadId);
            followUpQueueWorkspace.Remove(thread.ThreadId);
            if (followUpDispatchGates.Remove(thread.ThreadId, out var dispatchGate))
            {
                dispatchGate.Dispose();
            }
            settings.ComposerAttachmentDrafts.RemoveAll(draft =>
                string.Equals(draft.ThreadId, thread.ThreadId, StringComparison.Ordinal));
            if (!threadStore.Delete(settings, thread.ThreadId))
            {
                throw new InvalidOperationException($"Chat '{thread.ThreadId}' was not found.");
            }

            activeThreadId = null;
            activeTurnId = null;
            activeThreadLoaded = false;
            var nextThreadId = threadStore.GetThreads(settings, thread.ScopeKey)
                .FirstOrDefault()?.ThreadId;
            RefreshProjectThreads(nextThreadId, preserveCurrentSelection: false);
            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
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
        try
        {
            var guidance = SteeringText.Trim();
            var attachments = TaskWorkspace.Attachments.Select(attachment => attachment.Clone()).ToList();
            var queue = followUpQueueWorkspace.GetOrCreate(threadId);
            queue.Enqueue(guidance, CaptureQueuedTurnOptions(GetWorkspacePathForThread(threadId)), attachments);
            TaskWorkspace.NotifyQueuedFollowUpsChanged();
            await PersistFollowUpQueueAsync(threadId).ConfigureAwait(true);
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

    private async Task PersistSelectedFollowUpQueueAsync()
    {
        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            return;
        }

        await PersistFollowUpQueueAsync(activeThreadId).ConfigureAwait(true);
        RaiseThreadCommandStates();
    }

    private async Task SendQueuedFollowUpNowAsync(QueuedFollowUp item)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(activeThreadId) ||
            !followUpQueueWorkspace.ThreadIds.Contains(activeThreadId))
        {
            return;
        }

        var threadId = activeThreadId;
        var queue = followUpQueueWorkspace.GetRequired(threadId);
        if (queue.IndexOf(item.Id) < 0)
        {
            return;
        }

        if (IsTurnRunning)
        {
            if (appServerSessionCoordinator.State != AppServerSessionState.Connected || string.IsNullOrWhiteSpace(activeTurnId))
            {
                StatusMessage = "The active turn is not ready for steering";
                return;
            }

            try
            {
                var turnId = activeTurnId;
                await appServerSessionCoordinator.SteerTurnAsync(new CodexTurnSteerRequest(
                    threadId,
                    turnId,
                    BuildUserInputs(item.Text, item.Attachments, item.Options.WorkspacePath, item.Options.Model))).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    threadWorkspace.GetRequired(threadId).AddGuidance(item.Text);
                }
                queue.Remove(item.Id);
                await PersistFollowUpQueueAsync(threadId).ConfigureAwait(true);
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

        if (!ReferenceEquals(queue.Items.FirstOrDefault(), item))
        {
            StatusMessage = "Move this follow-up to the top before sending it";
            return;
        }

        if (item.State == QueuedFollowUpState.NeedsAttention)
        {
            queue.MarkPending(item.Id);
            await PersistFollowUpQueueAsync(threadId).ConfigureAwait(true);
        }
        await TryDrainFollowUpQueueAsync(threadId).ConfigureAwait(true);
    }

    private async Task SendSteerAsync()
    {
        if (!CanSteerTurn() || appServerSessionCoordinator.State != AppServerSessionState.Connected || activeThreadId is null || activeTurnId is null)
        {
            return;
        }

        var threadId = activeThreadId;
        var turnId = activeTurnId;
        try
        {
            var guidance = SteeringText.Trim();
            var attachments = TaskWorkspace.Attachments.Select(attachment => attachment.Clone()).ToList();
            await appServerSessionCoordinator.SteerTurnAsync(new CodexTurnSteerRequest(
                threadId,
                turnId,
                BuildUserInputs(guidance, attachments, GetActiveWorkspacePath()))).ConfigureAwait(true);
            var service = threadWorkspace.ThreadIds.Contains(threadId)
                ? threadWorkspace.GetRequired(threadId)
                : threadService;
            if (!string.IsNullOrWhiteSpace(guidance))
            {
                service.AddGuidance(guidance);
            }
            TaskWorkspace.NotifyResponseChanged();
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

    private ProjectThreadState CreateThreadState(
        ThreadScopeKey scope,
        string threadId,
        string title,
        string workspacePath,
        string? worktreeBranch = null)
    {
        var state = new ProjectThreadState
        {
            ScopeKind = scope.Kind,
            ProjectPath = scope.ProjectPath ?? string.Empty,
            ThreadId = threadId,
            Title = title,
            Mode = scope.Kind == ThreadScopeKind.General
                ? "general"
                : string.IsNullOrWhiteSpace(worktreeBranch) ? "local" : "worktree",
            WorkspacePath = Path.GetFullPath(workspacePath),
            WorktreeBranch = worktreeBranch,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        threadStore.Upsert(settings, state);
        threadStore.SetActive(settings, scope, threadId);
        threadWorkspace.Restore(state);
        followUpQueueWorkspace.Restore(threadId, state.QueuedFollowUps);
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

    private void OpenSettings()
    {
        IsDetailsPaneOpen = true;
        if (viewportWidth < 1000)
        {
            IsProjectRailOpen = false;
        }

        settings.IsProjectRailOpen = IsProjectRailOpen;
        settings.IsDetailsPaneOpen = true;
        _ = SaveLayoutSelectionAsync();
        if (appServerSessionCoordinator.State == AppServerSessionState.Connected)
        {
            var policyCwd = GetActiveWorkspacePathIfAvailable();
            if (!executionPolicyLoaded || !string.Equals(executionPolicyCwd, policyCwd, StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshExecutionPolicyAsync(policyCwd, appServerWarmUpCancellation.Token);
            }
        }
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
        IsGeneral: string.IsNullOrWhiteSpace(SelectedProjectPath));

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

        if (string.IsNullOrWhiteSpace(PromptText) && !TaskWorkspace.HasAttachments)
        {
            StatusMessage = "Enter a prompt or attach an image before starting a Codex task";
            return;
        }

        if (!TaskWorkspace.CanSubmitAttachments)
        {
            StatusMessage = TaskWorkspace.AttachmentValidationMessage;
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
            var submittedImages = TaskWorkspace.Attachments.Select(image => image.Clone()).ToList();
            TaskWorkspace.SubmittedPrompt = submittedPrompt;
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            activeThreadId = await EnsureActiveThreadAsync().ConfigureAwait(true);
            threadService.BeginTurn(submittedPrompt, submittedImages);
            TaskWorkspace.NotifyResponseChanged();
            if (SelectedThread is not null)
            {
                SelectedThread.Preview = string.IsNullOrWhiteSpace(submittedPrompt)
                    ? $"{submittedImages.Count} image{(submittedImages.Count == 1 ? string.Empty : "s")}"
                    : submittedPrompt;
            }

            var persistedThread = settings.ProjectThreads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, activeThreadId, StringComparison.Ordinal));
            if (persistedThread is not null)
            {
                persistedThread.Preview = string.IsNullOrWhiteSpace(submittedPrompt)
                    ? $"{submittedImages.Count} image{(submittedImages.Count == 1 ? string.Empty : "s")}"
                    : submittedPrompt;
            }

            var workspacePath = GetActiveWorkspacePath();
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            settings.LastServiceTierOverride = ToSettingsValue(TaskWorkspace.ServiceTierSelection);
            var turn = await appServerSessionCoordinator
                .StartTurnAsync(CreateTurnStartRequest(activeThreadId, submittedPrompt, submittedImages, workspacePath))
                .ConfigureAwait(true);

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
            TaskWorkspace.ClearAttachments();
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
        var editingService = threadService;
        if (string.IsNullOrWhiteSpace(threadId) ||
            !editingService.ConversationTurns.Any(turn => ReferenceEquals(turn, sourceTurn)))
        {
            StatusMessage = "The prompt is no longer part of the selected thread";
            return false;
        }

        var rollbackCount = editingService.GetActiveRollbackTurnCount(sourceTurn);
        if (rollbackCount < 1)
        {
            StatusMessage = "The selected prompt cannot be edited";
            return false;
        }

        var submittedPrompt = editedPrompt.Trim();
        var submittedAttachments = sourceTurn.UserAttachments.Select(attachment => attachment.Clone()).ToList();
        var workspacePath = GetActiveWorkspacePath();
        CodexTurnStartRequest startRequest;
        try
        {
            startRequest = CreateTurnStartRequest(threadId, submittedPrompt, submittedAttachments, workspacePath);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return false;
        }

        var editCommitted = false;
        StatusMessage = "Rewinding Codex thread for edited prompt";
        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            var rollback = await appServerSessionCoordinator
                .RollbackThreadAsync(new CodexThreadRollbackRequest(threadId, rollbackCount))
                .ConfigureAwait(true);
            if (!string.Equals(rollback.ThreadId, threadId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex returned a different thread after editing the prompt.");
            }

            editingService.SupersedeTurnsFrom(sourceTurn);
            editingService.ReconcileHistory(rollback.Turns);
            editingService.BeginTurn(submittedPrompt, submittedAttachments);
            editCommitted = true;
            var remainsSelected = string.Equals(activeThreadId, threadId, StringComparison.Ordinal) &&
                ReferenceEquals(threadService, editingService);
            if (remainsSelected)
            {
                TaskWorkspace.SubmittedPrompt = submittedPrompt;
                TaskWorkspace.NotifyResponseChanged();
            }

            if (SelectedThread is not null && string.Equals(SelectedThread.ThreadId, threadId, StringComparison.Ordinal))
            {
                SelectedThread.Preview = submittedPrompt;
            }
            var persistedThread = settings.ProjectThreads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
            if (persistedThread is not null)
            {
                persistedThread.Preview = submittedPrompt;
            }

            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            settings.LastServiceTierOverride = ToSettingsValue(TaskWorkspace.ServiceTierSelection);
            var startedTurn = await appServerSessionCoordinator.StartTurnAsync(startRequest).ConfigureAwait(true);
            var boundTurn = editingService.BindPendingTurn(startedTurn.TurnId);
            threadWorkspace.RegisterTurn(threadId, startedTurn.TurnId);
            var isSelectedAfterStart = string.Equals(activeThreadId, threadId, StringComparison.Ordinal) &&
                ReferenceEquals(threadService, editingService);
            if (boundTurn.Status == CodexTurnStatus.Running)
            {
                runningThreadIds.Add(threadId);
                UpdateThreadActivity(threadId, isRunning: true, "Running");
                activeTurnIds[threadId] = startedTurn.TurnId;
                if (isSelectedAfterStart)
                {
                    IsTurnRunning = true;
                    activeTurnId = startedTurn.TurnId;
                }
            }
            else
            {
                activeTurnIds.Remove(threadId);
                runningThreadIds.Remove(threadId);
                if (isSelectedAfterStart)
                {
                    activeTurnId = null;
                    IsTurnRunning = false;
                }
            }
            cancelTurnCommand.RaiseCanExecuteChanged();
            StatusMessage = boundTurn.Status == CodexTurnStatus.Running
                ? "Edited prompt running"
                : $"Edited prompt {boundTurn.Status.ToString().ToLowerInvariant()}";
            return true;
        }
        catch (Exception ex)
        {
            if (editCommitted)
            {
                editingService.FailPendingTurn(ex.Message);
                await SaveThreadStateAsync(threadId, editingService).ConfigureAwait(true);
            }
            runningThreadIds.Remove(threadId);
            activeTurnIds.Remove(threadId);
            if (string.Equals(activeThreadId, threadId, StringComparison.Ordinal) && ReferenceEquals(threadService, editingService))
            {
                IsTurnRunning = false;
                TaskWorkspace.NotifyResponseChanged();
            }
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "prompt_edit_failed", "Could not edit and resubmit the selected prompt.", exception: ex);
            return editCommitted;
        }
    }

    private async Task<string> EnsureActiveThreadAsync()
    {
        var scope = GetCurrentScope();
        var workspacePath = GetWorkspacePath(scope);

        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            var thread = await appServerSessionCoordinator
                .StartThreadAsync(CreateThreadStartOptions(workspacePath))
                .ConfigureAwait(true);
            AssistantWorktree? worktree = null;
            if (scope.Kind == ThreadScopeKind.Project &&
                string.Equals(NewThreadWorkspaceMode, "New worktree", StringComparison.Ordinal))
            {
                var repository = await gitService.GetRepositoryStateAsync(scope.ProjectPath!).ConfigureAwait(true);
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
                scope,
                thread.ThreadId,
                $"Thread {ProjectThreads.Count + 1}",
                worktree?.Path ?? workspacePath,
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
            var resumed = await appServerSessionCoordinator
                .ResumeThreadAsync(CreateThreadResumeRequest(activeThreadId, GetActiveWorkspacePath()))
                .ConfigureAwait(true);
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

            var thread = await appServerSessionCoordinator
                .StartThreadAsync(CreateThreadStartOptions(workspacePath))
                .ConfigureAwait(true);
            CreateThreadState(scope, thread.ThreadId, $"Thread {ProjectThreads.Count + 1}", workspacePath);
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
        ApprovalQueue.Clear();
        appServerSessionCoordinator.ServerRequestReceived -= OnServerRequestReceived;
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
        TaskWorkspace.SetModelCatalogLoading();

        try
        {
            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            CodexAccountInfo? account = null;
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
            var models = await appServerSessionCoordinator.ListModelsAsync().ConfigureAwait(true);
            TaskWorkspace.ApplyModelCatalog(models, account);

            StatusMessage = TaskWorkspace.ModelCatalog.Count == 0
                ? "No Codex models returned"
                : $"Loaded {TaskWorkspace.ModelCatalog.Count} Codex models";
        }
        catch (Exception ex)
        {
            TaskWorkspace.SetModelCatalogError(ex.Message);
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
        currentCodex.IsFound &&
        currentAuth.Readiness is not (AuthReadiness.Unavailable or AuthReadiness.NotSignedIn);

    private bool CanCreateThreadInCurrentScope() =>
        CanManageThreads() &&
        (!string.IsNullOrWhiteSpace(SelectedProjectPath) || !string.IsNullOrWhiteSpace(generalWorkspacePath));

    private bool CanCreateGeneralThread() =>
        CanManageThreads() && !string.IsNullOrWhiteSpace(generalWorkspacePath);

    private bool CanUseSelectedThread() => CanManageThreads() && SelectedThread is not null;

    private bool CanArchiveSelectedThread() =>
        CanUseSelectedThread() &&
        SelectedThread?.IsArchived == false &&
        !IsTurnRunning &&
        !SelectedThreadHasQueuedFollowUps();

    private bool CanUnarchiveSelectedThread() =>
        CanUseSelectedThread() && SelectedThread?.IsArchived == true;

    private bool CanToggleSelectedThreadPin() =>
        !IsShuttingDown && SelectedThread is not null;

    private bool CanDeleteSelectedThread() =>
        !IsShuttingDown &&
        SelectedThread is { IsRunning: false } &&
        !IsTurnRunning &&
        !SelectedThreadHasQueuedFollowUps();

    private bool CanRemoveSelectedWorktree() =>
        CanUseSelectedThread() &&
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

        return followUpQueueWorkspace.ThreadIds.Contains(threadId)
            ? followUpQueueWorkspace.GetRequired(threadId).Items.Count > 0
            : SelectedThread?.QueuedFollowUps.Count > 0;
    }

    private bool CanSteerTurn() =>
        !IsShuttingDown &&
        IsTurnRunning &&
        appServerSessionCoordinator.State == AppServerSessionState.Connected &&
        !string.IsNullOrWhiteSpace(activeThreadId) &&
        !string.IsNullOrWhiteSpace(activeTurnId) &&
        (!string.IsNullOrWhiteSpace(SteeringText) || TaskWorkspace.HasAttachments) &&
        TaskWorkspace.CanSubmitAttachments;

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

    private CodexThreadStartOptions CreateThreadStartOptions(string cwd)
    {
        var permissions = ResolvePermissionPolicy();
        return new CodexThreadStartOptions(
            NormalizeOverride(ModelOverride),
            permissions.Sandbox,
            permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer,
            permissions.PermissionProfileId,
            cwd);
    }

    private CodexThreadResumeRequest CreateThreadResumeRequest(string threadId, string cwd)
    {
        var permissions = ResolvePermissionPolicy();
        return new CodexThreadResumeRequest(
            threadId,
            cwd,
            permissions.Sandbox,
            NormalizeOverride(ModelOverride),
            permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer,
            permissions.PermissionProfileId);
    }

    private CodexThreadForkRequest CreateThreadForkRequest(string threadId, string cwd)
    {
        var permissions = ResolvePermissionPolicy();
        return new CodexThreadForkRequest(
            threadId,
            cwd,
            permissions.Sandbox,
            NormalizeOverride(ModelOverride),
            permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer,
            permissions.PermissionProfileId);
    }

    private IReadOnlyList<CodexUserInput> BuildUserInputs(
        string text,
        IReadOnlyList<AttachmentReference> attachments,
        string workspacePath,
        string? model = null)
    {
        var selectedModel = string.IsNullOrWhiteSpace(model)
            ? TaskWorkspace.SelectedModel
            : TaskWorkspace.ModelCatalog.FirstOrDefault(item =>
                string.Equals(item.Model, model, StringComparison.OrdinalIgnoreCase));
        if (attachments.Any(attachment => attachment.IsImage) && selectedModel?.SupportsImageInput == false)
        {
            throw new InvalidOperationException(
                $"{selectedModel.DisplayName} does not accept image input. Remove the images or choose an image-capable model.");
        }

        return new AttachmentPromptInputBuilder(attachmentStore, workspaceAttachmentResolver)
            .Build(text, attachments, workspacePath);
    }

    private CodexTurnStartRequest CreateTurnStartRequest(
        string threadId,
        string prompt,
        IReadOnlyList<AttachmentReference> attachments,
        string cwd)
    {
        var permissions = ResolvePermissionPolicy();
        return new CodexTurnStartRequest(
            threadId,
            BuildUserInputs(prompt, attachments, cwd),
            cwd,
            permissions.Sandbox,
            NormalizeOverride(ModelOverride),
            ParseReasoningEffort(ReasoningEffortOverride),
            TaskWorkspace.ServiceTierSelection,
            permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer,
            permissions.PermissionProfileId);
    }

    private QueuedTurnOptionsSnapshot CaptureQueuedTurnOptions(string workspacePath)
    {
        var permissions = ResolvePermissionPolicy();
        return new QueuedTurnOptionsSnapshot
        {
            WorkspacePath = workspacePath,
            Model = NormalizeOverride(ModelOverride),
            ReasoningEffort = ParseReasoningEffort(ReasoningEffortOverride),
            ServiceTier = TaskWorkspace.ServiceTierSelection,
            Sandbox = permissions.Sandbox,
            ApprovalPolicy = permissions.ApprovalPolicy,
            ApprovalsReviewer = permissions.ApprovalsReviewer,
            PermissionProfileId = permissions.PermissionProfileId
        };
    }

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

    private SemaphoreSlim GetFollowUpDispatchGate(string threadId)
    {
        if (!followUpDispatchGates.TryGetValue(threadId, out var gate))
        {
            gate = new SemaphoreSlim(1, 1);
            followUpDispatchGates.Add(threadId, gate);
        }

        return gate;
    }

    private async Task TryDrainFollowUpQueueAsync(string threadId)
    {
        if (IsShuttingDown ||
            runningThreadIds.Contains(threadId) ||
            !followUpQueueWorkspace.ThreadIds.Contains(threadId))
        {
            return;
        }

        var queue = followUpQueueWorkspace.GetRequired(threadId);
        if (queue.Items.FirstOrDefault() is not { State: QueuedFollowUpState.Pending } item)
        {
            return;
        }

        var gate = GetFollowUpDispatchGate(threadId);
        await gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (IsShuttingDown || runningThreadIds.Contains(threadId) ||
                queue.Items.FirstOrDefault() is not { State: QueuedFollowUpState.Pending } head ||
                !string.Equals(head.Id, item.Id, StringComparison.Ordinal))
            {
                return;
            }

            await StartQueuedFollowUpAsync(threadId, queue, head).ConfigureAwait(true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StartQueuedFollowUpAsync(
        string threadId,
        CodexFollowUpQueue queue,
        QueuedFollowUp item)
    {
        var service = threadWorkspace.GetRequired(threadId);
        queue.MarkStarting(item.Id);
        TaskWorkspace.NotifyQueuedFollowUpsChanged();
        await PersistFollowUpQueueAsync(threadId).ConfigureAwait(true);

        try
        {
            var options = item.Options;
            var workspacePath = Path.GetFullPath(options.WorkspacePath);
            if (!Directory.Exists(workspacePath))
            {
                throw new InvalidOperationException($"The queued follow-up workspace is unavailable: {workspacePath}");
            }

            await EnsureAppServerSessionAsync().ConfigureAwait(true);
            service.BeginTurn(item.Text, item.Attachments);
            if (string.Equals(threadId, activeThreadId, StringComparison.Ordinal))
            {
                TaskWorkspace.NotifyResponseChanged();
            }

            var persisted = settings.ProjectThreads.First(thread =>
                string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
            persisted.Preview = item.Text;
            var turn = await appServerSessionCoordinator.StartTurnAsync(new CodexTurnStartRequest(
                threadId,
                BuildUserInputs(item.Text, item.Attachments, workspacePath, options.Model),
                workspacePath,
                options.Sandbox,
                options.Model,
                options.ReasoningEffort,
                options.ServiceTier,
                options.ApprovalPolicy,
                options.ApprovalsReviewer,
                options.PermissionProfileId)).ConfigureAwait(true);

            var boundTurn = service.BindPendingTurn(turn.TurnId);
            threadWorkspace.RegisterTurn(threadId, turn.TurnId);
            queue.Remove(item.Id);
            await PersistFollowUpQueueAsync(threadId).ConfigureAwait(true);
            if (boundTurn.Status == CodexTurnStatus.Running)
            {
                runningThreadIds.Add(threadId);
                activeTurnIds[threadId] = turn.TurnId;
                UpdateThreadActivity(threadId, isRunning: true, "Running");
                if (string.Equals(threadId, activeThreadId, StringComparison.Ordinal))
                {
                    activeTurnId = turn.TurnId;
                    IsTurnRunning = true;
                    StatusMessage = "Queued follow-up running";
                }
            }
            else
            {
                runningThreadIds.Remove(threadId);
                activeTurnIds.Remove(threadId);
                if (string.Equals(threadId, activeThreadId, StringComparison.Ordinal))
                {
                    activeTurnId = null;
                    IsTurnRunning = false;
                }
                _ = TryDrainFollowUpQueueAsync(threadId);
            }

            TaskWorkspace.NotifyQueuedFollowUpsChanged();
            RaiseThreadCommandStates();
        }
        catch (Exception ex)
        {
            service.FailPendingTurn(ex.Message);
            queue.MarkNeedsAttention(item.Id, ex.Message);
            await PersistFollowUpQueueAsync(threadId).ConfigureAwait(true);
            TaskWorkspace.NotifyQueuedFollowUpsChanged();
            StatusMessage = ex.Message;
            logger.Log(
                AppLogLevel.Error,
                "queued_follow_up_start_failed",
                "A queued follow-up could not be started and requires attention.",
                new Dictionary<string, string?> { ["threadId"] = threadId, ["itemId"] = item.Id },
                ex);
        }
    }

    private async Task PersistFollowUpQueueAsync(string threadId)
    {
        if (!followUpQueueWorkspace.ThreadIds.Contains(threadId))
        {
            return;
        }

        var snapshots = followUpQueueWorkspace.GetRequired(threadId).Snapshot().Select(item => item.Clone()).ToList();
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted is null)
        {
            return;
        }

        persisted.QueuedFollowUps = snapshots;
        persisted.UpdatedAt = DateTimeOffset.UtcNow;
        var presentation = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (presentation is not null)
        {
            presentation.QueuedFollowUps = snapshots.Select(item => item.Clone()).ToList();
            presentation.UpdatedAt = persisted.UpdatedAt;
        }

        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
    }

    private async Task SaveThreadStateAndMaybeDrainAsync(
        string threadId,
        CodexThreadService service,
        bool shouldDrain)
    {
        await SaveThreadStateAsync(threadId, service).ConfigureAwait(true);
        if (shouldDrain)
        {
            await TryDrainFollowUpQueueAsync(threadId).ConfigureAwait(true);
        }
    }

    private async Task EnsureAppServerSessionAsync(CancellationToken cancellationToken = default)
    {
        await appServerSessionCoordinator.EnsureConnectedAsync(currentCodex, cancellationToken).ConfigureAwait(true);
    }

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
            foreach (var threadId in runningThreadIds.ToArray())
            {
                UpdateThreadActivity(threadId, isRunning: false, "Recovery needed");
            }

            runningThreadIds.Clear();
            activeTurnIds.Clear();
            loadedThreadIds.Clear();
            activeThreadLoaded = false;
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

    private void OnAppServerNotificationReceived(object? sender, AppServerNotification notification)
    {
        DispatchAppServerNotification(notification);
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
        if (notification.Method == "serverRequest/resolved" && TryReadRequestId(notification.Params, out var requestId))
        {
            if (ApprovalQueue.Resolve(requestId))
            {
                StatusMessage = ApprovalQueue.HasPendingApproval
                    ? $"Approval required: {ApprovalQueue.ActivePrompt?.Kind}"
                    : "Approval request resolved";
            }
            return;
        }

        if (Account.TryApplyNotification(notification))
        {
            if (notification.Method is "account/updated" or "account/login/completed")
            {
                TaskWorkspace.InvalidateModelCatalog();
            }
            return;
        }

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
                _ = SaveThreadStateAndMaybeDrainAsync(
                    routedThreadId,
                    routedService,
                    routedService.ActiveTurnStatus == CodexTurnStatus.Completed);
            }
            _ = Git.RefreshAsync();
        }

        if (!string.IsNullOrWhiteSpace(routedThreadId) &&
            notification.Method is "thread/archived" or "thread/unarchived" &&
            settings.ProjectThreads.Any(thread => string.Equals(thread.ThreadId, routedThreadId, StringComparison.Ordinal)))
        {
            threadStore.SetArchived(
                settings,
                routedThreadId,
                archived: notification.Method == "thread/archived");
        }

        TaskWorkspace.NotifyResponseChanged();
        OnPropertyChanged(nameof(FinalResponse));
        RaiseThreadCommandStates();
    }

    private static bool TryReadRequestId(System.Text.Json.Nodes.JsonObject parameters, out CodexRequestId requestId)
    {
        var node = parameters["requestId"] ?? parameters["id"];
        if (node is System.Text.Json.Nodes.JsonValue value)
        {
            if (value.TryGetValue<long>(out var integerId))
            {
                requestId = CodexRequestId.FromInteger(integerId);
                return true;
            }

            if (value.TryGetValue<string>(out var stringId) && !string.IsNullOrEmpty(stringId))
            {
                requestId = CodexRequestId.FromString(stringId);
                return true;
            }
        }

        requestId = default;
        return false;
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
        persisted.ContextTokensUsed = service.ContextTokensUsed;
        persisted.ContextWindowTokens = service.ContextWindowTokens;
        persisted.ContextCompactionCount = service.ContextCompactionCount;
        persisted.UpdatedAt = DateTimeOffset.UtcNow;
        var presentation = ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (presentation is not null)
        {
            presentation.FinalResponse = persisted.FinalResponse;
            presentation.TimelineItems = [.. persisted.TimelineItems];
            presentation.RawEvents = [.. persisted.RawEvents];
            presentation.ConversationTurns = persisted.ConversationTurns.Select(CloneConversationTurn).ToList();
            presentation.ContextTokensUsed = persisted.ContextTokensUsed;
            presentation.ContextWindowTokens = persisted.ContextWindowTokens;
            presentation.ContextCompactionCount = persisted.ContextCompactionCount;
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
        var scope = string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? ThreadScopeKey.General
            : ThreadScopeKey.ForProject(SelectedProjectPath);
        foreach (var persisted in threadStore.GetThreads(settings, scope))
        {
            ProjectThreads.Add(persisted);
            threadWorkspace.Restore(persisted);
            followUpQueueWorkspace.Restore(persisted.ThreadId, persisted.QueuedFollowUps);
        }

        SelectedThread = threadStore.GetActive(settings, scope);
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
        return SelectedThread ?? threadStore.GetActive(settings, GetCurrentScope());
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
        foreach (var thread in threadStore.GetThreads(settings, scope))
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
        Terminal.RefreshContext();
        _ = Git.RefreshAsync();
    }

    private void SelectThread(ProjectThreadState? state)
    {
        var previousActiveThreadId = activeThreadId;
        CaptureAttachmentDraft(SelectedProjectPath, previousActiveThreadId);
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

        activeThreadLoaded = state is not null && loadedThreadIds.Contains(state.ThreadId);
        activeTurnId = state is not null && activeTurnIds.TryGetValue(state.ThreadId, out var turnId) ? turnId : null;
        TaskWorkspace.SubmittedPrompt = state?.Preview ?? string.Empty;
        IsTurnRunning = state is not null && runningThreadIds.Contains(state.ThreadId);

        if (state is null)
        {
            threadService = new CodexThreadService();
            threadService.Reset();
            TaskWorkspace.UseFollowUpQueue(new CodexFollowUpQueue());
        }
        else
        {
            threadService = threadWorkspace.ThreadIds.Contains(state.ThreadId)
                ? threadWorkspace.GetRequired(state.ThreadId)
                : threadWorkspace.Restore(state);
            TaskWorkspace.UseFollowUpQueue(followUpQueueWorkspace.ThreadIds.Contains(state.ThreadId)
                ? followUpQueueWorkspace.GetRequired(state.ThreadId)
                : followUpQueueWorkspace.Restore(state.ThreadId, state.QueuedFollowUps));
            if (!state.IsArchived)
            {
                threadStore.SetActive(settings, state.ScopeKey, state.ThreadId);
            }
        }

        RestoreAttachmentDraft(SelectedProjectPath, activeThreadId);

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
            var persisted = FindProjectThreadState();
            if (persisted is null)
            {
                var scope = GetCurrentScope();
                persisted = new ProjectThreadState
                {
                    ScopeKind = scope.Kind,
                    ProjectPath = scope.ProjectPath ?? string.Empty,
                    ThreadId = activeThreadId,
                    Title = $"Thread {ProjectThreads.Count + 1}",
                    Mode = scope.Kind == ThreadScopeKind.General ? "general" : "local",
                    WorkspacePath = GetActiveWorkspacePath()
                };
                threadStore.Upsert(settings, persisted);
            }

            persisted.ThreadId = activeThreadId;
            persisted.FinalResponse = threadService.FinalResponse;
            persisted.TimelineItems = [.. threadService.TimelineItems.TakeLast(100)];
            persisted.RawEvents = [.. threadService.RawEvents.TakeLast(100)];
            persisted.ConversationTurns = threadService.SnapshotConversation().Select(CloneConversationTurn).ToList();
            persisted.ContextTokensUsed = threadService.ContextTokensUsed;
            persisted.ContextWindowTokens = threadService.ContextWindowTokens;
            persisted.ContextCompactionCount = threadService.ContextCompactionCount;
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
        threads.AddRange(string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? ProjectThreads
            : threadStore.GetThreads(settings, ThreadScopeKey.General));
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
        Activity = [.. source.Activity],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())]
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
}
