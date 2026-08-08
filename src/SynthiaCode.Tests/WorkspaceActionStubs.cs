using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Core.Workspaces;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex.Configuration;
using SynthiaCode.Harnesses.Codex;

/// <summary>Small contract stubs used by presentation tests; production has no delegate adapters.</summary>
internal sealed class TaskConversationActionStub : ITurnExecutionActions, IFollowUpManagementActions,
    IConversationHistoryActions, IComposerSupportActions, IAgentManagementActions, IGoalManagementActions
{
    public Func<Task> Submit { get; init; } = () => Task.CompletedTask;
    public Func<Task> Cancel { get; init; } = () => Task.CompletedTask;
    public Func<Task> LoadModels { get; init; } = () => Task.CompletedTask;
    public Func<Task> Steer { get; init; } = () => Task.CompletedTask;
    public Func<bool> CanSubmit { get; init; } = () => true;
    public Func<bool> CanCancel { get; init; } = () => false;
    public Func<bool> CanSteer { get; init; } = () => false;
    public Func<bool> CanLoad { get; init; } = () => true;
    public Func<bool> CanFork { get; init; } = () => true;
    public Action<Uri> OpenUri { get; init; } = _ => { };
    public Func<Task> AlternateFollowUp { get; init; } = () => Task.CompletedTask;
    public Func<Task> PersistQueue { get; init; } = () => Task.CompletedTask;
    public Func<QueuedFollowUp, Task> SendQueued { get; init; } = _ => Task.CompletedTask;
    public Func<CodexConversationTurn, string, Task<bool>> EditPrompt { get; init; } = (_, _) => Task.FromResult(false);
    public Action<string> ShowImage { get; init; } = _ => { };
    public Func<string, Task> EditImage { get; init; } = _ => Task.CompletedTask;
    public Func<string, Task> Fork { get; init; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task<ComposerSkillLoadResult>> LoadSkills { get; init; } =
        _ => Task.FromResult(new ComposerSkillLoadResult([], false, null));
    public Func<string, Task<CodexThreadGoal>> SetGoal { get; init; } = objective =>
        Task.FromResult(Goal("thread-stub", objective, CodexThreadGoalStatus.Active));
    public Func<CodexThreadGoalStatus, Task<CodexThreadGoal>> SetGoalStatus { get; init; } = status =>
        Task.FromResult(Goal("thread-stub", "Stub goal", status));
    public Func<Task<bool>> ClearGoal { get; init; } = () => Task.FromResult(true);
    public Func<string, string, Task> StartGoal { get; init; } = (_, _) => Task.CompletedTask;
    public Func<bool> CanGoal { get; init; } = () => true;

    public Task SubmitAsync() => Submit();
    public Task CancelAsync() => Cancel();
    public Task LoadModelsAsync() => LoadModels();
    public Task SteerAsync() => Steer();
    public bool CanSubmitTurn() => CanSubmit();
    public bool CanCancelTurn() => CanCancel();
    public bool CanSteerTurn() => CanSteer();
    public bool CanLoadModels() => CanLoad();
    public void OpenExternalUri(Uri uri) => OpenUri(uri);
    public Task SendAlternateFollowUpAsync() => AlternateFollowUp();
    public Task PersistFollowUpQueueAsync(IReadOnlyList<QueuedFollowUpSnapshot> snapshots) => PersistQueue();
    public Task SendQueuedFollowUpAsync(string followUpId) => SendQueued(CreateQueuedFollowUp(followUpId));
    public Task<bool> EditPromptAsync(CodexConversationTurn turn, string editedPrompt) => EditPrompt(turn, editedPrompt);
    public void ShowImagePreview(string path) => ShowImage(path);
    public Task EditGeneratedImageAsync(string path) => EditImage(path);
    public Task ForkConversationAsync(string turnId) => Fork(turnId);
    public bool CanForkConversation() => CanFork();
    public Task<ComposerSkillLoadResult> LoadComposerSkillsAsync(CancellationToken cancellationToken) => LoadSkills(cancellationToken);
    public Task<CodexThreadReadResult> ReadAgentThreadAsync(string threadId) =>
        Task.FromResult(new CodexThreadReadResult(threadId, []));
    public Task SteerAgentAsync(string threadId, string turnId, string message) => Task.CompletedTask;
    public Task StopAgentAsync(string threadId, string turnId) => Task.CompletedTask;
    public Task<CodexThreadGoal> SetGoalAsync(string objective) => SetGoal(objective);
    public Task<CodexThreadGoal> SetGoalStatusAsync(CodexThreadGoalStatus status) => SetGoalStatus(status);
    public Task<bool> ClearGoalAsync() => ClearGoal();
    public Task StartGoalWorkAsync(string threadId, string objective) => StartGoal(threadId, objective);
    public bool CanManageGoal() => CanGoal();

    private static CodexThreadGoal Goal(string threadId, string objective, CodexThreadGoalStatus status) =>
        new(threadId, objective, status, 0, 0, 1, 1);

    private static QueuedFollowUp CreateQueuedFollowUp(string followUpId)
    {
        var queue = new CodexFollowUpQueue();
        queue.Restore([new QueuedFollowUpSnapshot { Id = followUpId, Text = "stub" }]);
        return queue.Items[0];
    }
}

internal sealed class ProjectThreadActionStub : IProjectNavigationActions, IThreadLifecycleActions
{
    public Func<Task> Browse { get; init; } = () => Task.CompletedTask;
    public Func<object?, Task> OpenRecent { get; init; } = _ => Task.CompletedTask;
    public Func<object?, Task> EditProject { get; init; } = _ => Task.CompletedTask;
    public Func<Task> Create { get; init; } = () => Task.CompletedTask;
    public Func<Task> CreateGeneral { get; init; } = () => Task.CompletedTask;
    public Func<Task> CreateProject { get; init; } = () => Task.CompletedTask;
    public Func<Task> Resume { get; init; } = () => Task.CompletedTask;
    public Func<Task> Fork { get; init; } = () => Task.CompletedTask;
    public Func<Task> Archive { get; init; } = () => Task.CompletedTask;
    public Func<Task> Unarchive { get; init; } = () => Task.CompletedTask;
    public Func<Task> RemoveWorktree { get; init; } = () => Task.CompletedTask;
    public Func<bool> CanCreate { get; init; } = () => false;
    public Func<bool> CanCreateGeneral { get; init; } = () => false;
    public Predicate<object?> CanEditProject { get; init; } = _ => false;
    public Func<bool> CanUse { get; init; } = () => false;
    public Func<bool> CanFork { get; init; } = () => false;
    public Func<bool> CanArchive { get; init; } = () => false;
    public Func<bool> CanUnarchive { get; init; } = () => false;
    public Func<bool> CanRemove { get; init; } = () => false;
    public Action<ProjectThreadState?> SelectionChanged { get; init; } = _ => { };
    public Func<Task> TogglePin { get; init; } = () => Task.CompletedTask;
    public Func<Task> Delete { get; init; } = () => Task.CompletedTask;
    public Func<bool> CanTogglePin { get; init; } = () => false;
    public Func<bool> CanDelete { get; init; } = () => false;
    public Func<Task> Rename { get; init; } = () => Task.CompletedTask;
    public Func<bool> CanRename { get; init; } = () => false;

    public Task BrowseProjectAsync() => Browse(); public Task OpenRecentProjectAsync(object? parameter) => OpenRecent(parameter); public Task EditProjectAsync(object? parameter) => EditProject(parameter);
    public Task CreateThreadAsync() => Create(); public Task CreateGeneralThreadAsync() => CreateGeneral(); public Task CreateProjectThreadAsync() => CreateProject();
    public Task ResumeThreadAsync() => Resume(); public Task ForkThreadAsync() => Fork(); public Task ArchiveThreadAsync() => Archive(); public Task UnarchiveThreadAsync() => Unarchive(); public Task RemoveWorktreeAsync() => RemoveWorktree();
    public bool CanCreateThread() => CanCreate(); public bool CanCreateGeneralThread() => CanCreateGeneral(); bool IProjectNavigationActions.CanEditProject(object? parameter) => CanEditProject.Invoke(parameter); public bool CanUseSelectedThread() => CanUse(); public bool CanForkSelectedThread() => CanFork(); public bool CanArchiveSelectedThread() => CanArchive(); public bool CanUnarchiveSelectedThread() => CanUnarchive(); public bool CanRemoveSelectedWorktree() => CanRemove();
    public void SelectedThreadChanged(ProjectThreadState? state) => SelectionChanged(state); public Task TogglePinThreadAsync() => TogglePin(); public Task DeleteThreadAsync() => Delete(); public bool CanTogglePinThread() => CanTogglePin(); public bool CanDeleteThread() => CanDelete(); public Task RenameThreadAsync() => Rename(); public bool CanRenameThread() => CanRename();
}

internal static class WorkspaceActionStubs
{
    public static TaskViewModel CreateTaskViewModel(TaskConversationActionStub actions) =>
        new(actions, actions, actions, actions, actions, goalActions: actions);

    public static ProjectThreadViewModel CreateProjectThreadViewModel(ProjectThreadActionStub actions) =>
        new(actions, actions);

    public static ConversationWorkspaceSnapshot Snapshot(CodexThreadService service) => new(
        service.ActiveThreadId,
        service.ActiveTurnId,
        service.ActiveTurnStatus,
        service.FinalResponse,
        service.RequiresAuthentication,
        service.ContextTokensUsed,
        service.ContextWindowTokens,
        service.ContextCompactionCount,
        service.TimelineItems.Select(item => item with { }).ToArray(),
        service.RawEvents.ToArray(),
        service.SnapshotConversation(),
        []);

    public static MainViewModel CreateMainViewModel(
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
        IProjectTrustService? projectTrustService = null,
        bool enableGoalMode = false)
    {
        var resolver = new WorkspaceAttachmentResolver();
        var harnessRuntime = new HarnessRuntimeCoordinator(new HarnessRegistry([
            new CodexHarness(codexDiscoveryService, appServerSessionCoordinator)
        ]));
        var harnessOperations = new HarnessOperations(harnessRuntime);
        var lifecycle = new ThreadLifecycleUseCaseService(
            harnessOperations, gitService, worktreeService, threadStore, threadWorkspace, settingsStore);
        var persistence = new ThreadStatePersistenceUseCaseService(settingsStore, threadStore, threadWorkspace);
        var queues = new CodexFollowUpQueueWorkspace();
        var workflow = new ConversationWorkflowController(threadStore, threadWorkspace, queues);
        var turns = new TurnExecutionUseCaseService(
            harnessOperations, workflow, lifecycle, persistence);
        var reviews = new CodeReviewUseCaseService(appServerSessionCoordinator, workflow);
        var queue = new FollowUpQueueUseCaseService(
            harnessOperations, workflow, settingsStore, threadWorkspace, queues);
        var attachments = new AttachmentDraftOrchestrationService(
            attachmentStore,
            resolver,
            new CodexTurnRequestFactory(attachmentStore, resolver),
            logger);
        return new MainViewModel(
            settingsStore, codexDiscoveryService, appServerSessionCoordinator, harnessRuntime,
            authService, folderPicker,
            userInteractionService, themeService, codexCliUtilityRunner, terminalService, logger,
            workflow, lifecycle, persistence, turns, reviews, queue, gitService,
            new ProjectWorkspaceOperations(gitService, worktreeService, recentProjectService, generalWorkspaceService),
            projectTrustService ?? new AllowAllProjectTrustService(),
            attachments,
            new SharedCodexConfigurationService(Path.Combine(Path.GetTempPath(), "synthiacode-tests-codex-home")),
            enableGoalMode: enableGoalMode);
    }

    private sealed class AllowAllProjectTrustService : IProjectTrustService
    {
        public Task<ProjectTrustAuthorizationResult> AuthorizeAsync(
            string projectPath,
            CodexInstallation installation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return global::System.Threading.Tasks.Task.FromResult(ProjectTrustAuthorizationResult.Authorized(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath)),
                CodexProjectTrustLevel.Trusted));
        }
    }

    public static TaskConversationActionStub Task(
        Func<Task> submit, Func<Task> cancel, Func<Task> loadModels, Func<Task> steer,
        Func<bool> canCancel, Func<bool> canSteer, Action<Uri>? openExternalUri = null,
        Func<Task>? alternateFollowUp = null, Func<Task>? persistFollowUpQueue = null,
        Func<QueuedFollowUp, Task>? sendQueuedFollowUp = null,
        Func<CodexConversationTurn, string, Task<bool>>? editPrompt = null,
        Action<string>? showLocalImage = null, Func<string, Task>? forkConversation = null,
        Func<CancellationToken, Task<ComposerSkillLoadResult>>? loadComposerSkills = null,
        Func<string, Task>? editGeneratedImage = null) => new()
    {
        Submit = submit, Cancel = cancel, LoadModels = loadModels, Steer = steer,
        CanCancel = canCancel, CanSteer = canSteer,
        OpenUri = openExternalUri ?? (_ => { }),
        AlternateFollowUp = alternateFollowUp ?? new Func<Task>(() => global::System.Threading.Tasks.Task.CompletedTask),
        PersistQueue = persistFollowUpQueue ?? new Func<Task>(() => global::System.Threading.Tasks.Task.CompletedTask),
        SendQueued = sendQueuedFollowUp ?? new Func<QueuedFollowUp, Task>(_ => global::System.Threading.Tasks.Task.CompletedTask),
        EditPrompt = editPrompt ?? new Func<CodexConversationTurn, string, Task<bool>>((_, _) => global::System.Threading.Tasks.Task.FromResult(false)),
        ShowImage = showLocalImage ?? (_ => { }),
        EditImage = editGeneratedImage ?? new Func<string, Task>(_ => global::System.Threading.Tasks.Task.CompletedTask),
        Fork = forkConversation ?? new Func<string, Task>(_ => global::System.Threading.Tasks.Task.CompletedTask),
        LoadSkills = loadComposerSkills ?? new Func<CancellationToken, Task<ComposerSkillLoadResult>>(_ => global::System.Threading.Tasks.Task.FromResult(new ComposerSkillLoadResult([], false, null)))
    };

    public static ProjectThreadActionStub Project(
        Func<Task> browseProject, Func<object?, Task> openRecentProject,
        Func<Task> createThread, Func<Task> createGeneralThread, Func<Task> createProjectThread,
        Func<Task> resumeThread, Func<Task> forkThread, Func<Task> archiveThread,
        Func<Task> unarchiveThread, Func<Task> removeWorktree, Func<bool> canCreateThread,
        Func<bool> canCreateGeneralThread, Func<bool> canUseSelectedThread, Func<bool> canArchiveSelectedThread,
        Func<bool> canUnarchiveSelectedThread, Func<bool> canRemoveWorktree, Action<ProjectThreadState?> selectionChanged,
        Func<Task>? togglePinThread = null, Func<Task>? deleteThread = null,
        Func<bool>? canTogglePinThread = null, Func<bool>? canDeleteThread = null,
        Func<Task>? renameThread = null, Func<bool>? canRenameThread = null) => new()
    {
        Browse = browseProject, OpenRecent = openRecentProject, Create = createThread, CreateGeneral = createGeneralThread,
        CreateProject = createProjectThread, Resume = resumeThread, Fork = forkThread, Archive = archiveThread,
        Unarchive = unarchiveThread, RemoveWorktree = removeWorktree, CanCreate = canCreateThread,
        CanCreateGeneral = canCreateGeneralThread, CanUse = canUseSelectedThread, CanFork = canUseSelectedThread, CanArchive = canArchiveSelectedThread,
        CanUnarchive = canUnarchiveSelectedThread, CanRemove = canRemoveWorktree, SelectionChanged = selectionChanged,
        TogglePin = togglePinThread ?? new Func<Task>(() => global::System.Threading.Tasks.Task.CompletedTask), Delete = deleteThread ?? new Func<Task>(() => global::System.Threading.Tasks.Task.CompletedTask),
        CanTogglePin = canTogglePinThread ?? (() => false), CanDelete = canDeleteThread ?? (() => false),
        Rename = renameThread ?? new Func<Task>(() => global::System.Threading.Tasks.Task.CompletedTask), CanRename = canRenameThread ?? new Func<bool>(() => false)
    };
}
