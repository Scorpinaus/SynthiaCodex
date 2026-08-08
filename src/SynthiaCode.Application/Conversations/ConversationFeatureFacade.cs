using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Worktrees;

namespace SynthiaCode.Application.Conversations;

/// <summary>
/// The presentation-facing boundary for the complete conversation feature slice.
/// It hides the individual lifecycle, execution, persistence, and queue services.
/// </summary>
public interface IConversationFeatureFacade : IAsyncDisposable
{
    event EventHandler<ConversationWorkspaceChangedEvent>? Changed;

    IConversationWorkspace Workspace { get; }

    Task<ThreadStartUseCaseResult> StartThreadAsync(ThreadStartUseCaseRequest request, CancellationToken cancellationToken = default);
    Task<ThreadResumeUseCaseResult> ResumeThreadAsync(ThreadResumeUseCaseRequest request, CancellationToken cancellationToken = default);
    Task<ThreadActivationUseCaseResult> ResumeOrReplaceThreadAsync(ThreadActivationUseCaseRequest request, CancellationToken cancellationToken = default);
    Task<ThreadForkResult> ForkThreadAsync(ThreadForkRequest request, CancellationToken cancellationToken = default);
    Task ArchiveThreadAsync(AppSettings settings, string threadId, HarnessConnectionOptions connectionOptions, CancellationToken cancellationToken = default);
    Task UnarchiveThreadAsync(AppSettings settings, string threadId, HarnessConnectionOptions connectionOptions, CancellationToken cancellationToken = default);
    Task<bool> SetThreadPinnedAsync(AppSettings settings, string threadId, bool pinned, CancellationToken cancellationToken = default);
    Task RenameThreadAsync(AppSettings settings, string threadId, string title, HarnessConnectionOptions connectionOptions, CancellationToken cancellationToken = default);
    Task DeleteThreadAsync(AppSettings settings, string threadId, bool archiveFirst, HarnessConnectionOptions connectionOptions, CancellationToken cancellationToken = default);
    Task RemoveWorktreeAsync(AppSettings settings, ProjectThreadState thread, string projectPath, CancellationToken cancellationToken = default);

    Task<TurnExecutionResult> StartTurnAsync(TurnExecutionRequest request, CancellationToken cancellationToken = default);
    Task<TurnEditExecutionResult> EditTurnAsync(TurnEditExecutionRequest request, CancellationToken cancellationToken = default);
    Task<ConversationWorkspaceSnapshot> SteerTurnAsync(string threadId, HarnessConnectionOptions connectionOptions, SteerTurnCommand command, string guidance, CancellationToken cancellationToken = default);
    Task CancelTurnAsync(HarnessConnectionOptions connectionOptions, ConversationAddress address, string remoteTurnId, CancellationToken cancellationToken = default);

    Task<ThreadStateSaveResult?> SaveThreadAsync(AppSettings settings, string threadId, CancellationToken cancellationToken = default);
    Task<ThreadStateSaveResult> SaveActiveThreadAsync(AppSettings settings, ProjectThreadState? selectedThread, ThreadScopeKey scope, string threadId, string workspacePath, string title, CancellationToken cancellationToken = default);
    Task SaveSelectionAsync(AppSettings settings, CancellationToken cancellationToken = default);

    bool HasFollowUpQueue(string threadId);
    int GetFollowUpCount(string threadId);
    IReadOnlyList<QueuedFollowUpSnapshot> GetFollowUpSnapshots(string threadId);
    QueuedFollowUpSnapshot? GetFirstPendingFollowUp(string threadId);
    QueuedFollowUpSnapshot? GetFollowUp(string threadId, string followUpId);
    bool ContainsFollowUp(string threadId, string followUpId);
    bool IsFirstFollowUp(string threadId, string followUpId);
    Task<FollowUpQueueMutationResult> EnqueueFollowUpAsync(FollowUpEnqueueUseCaseRequest request, CancellationToken cancellationToken = default);
    Task<FollowUpQueueMutationResult> ReplaceFollowUpsAsync(AppSettings settings, string threadId, IEnumerable<QueuedFollowUpSnapshot> snapshots, CancellationToken cancellationToken = default);
    Task<FollowUpQueueMutationResult> MarkFollowUpPendingAsync(AppSettings settings, string threadId, string followUpId, CancellationToken cancellationToken = default);
    Task<FollowUpQueueMutationResult> SteerFollowUpAsync(AppSettings settings, string threadId, string followUpId, HarnessConnectionOptions connectionOptions, SteerTurnCommand command, CancellationToken cancellationToken = default);
    Task<FollowUpDispatchUseCaseResult> DispatchNextFollowUpAsync(FollowUpDispatchUseCaseRequest request, CancellationToken cancellationToken = default);
    Task<FollowUpQueueMutationResult> PersistFollowUpsAsync(AppSettings settings, string threadId, CancellationToken cancellationToken = default);
    Task RemoveFollowUpsAsync(string threadId);
}

public sealed class ConversationFeatureFacade : IConversationFeatureFacade
{
    private readonly ConversationWorkflowController workspace;
    private readonly ThreadLifecycleUseCaseService lifecycle;
    private readonly ThreadStatePersistenceUseCaseService persistence;
    private readonly TurnExecutionUseCaseService turns;
    private readonly FollowUpQueueUseCaseService followUps;

    public ConversationFeatureFacade(
        IHarnessOperations harnesses,
        IGitService git,
        IWorktreeService worktrees,
        ISettingsStore settingsStore,
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace,
        CodexFollowUpQueueWorkspace followUpQueues)
    {
        workspace = new ConversationWorkflowController(threadStore, threadWorkspace, followUpQueues);
        lifecycle = new ThreadLifecycleUseCaseService(
            harnesses,
            git,
            worktrees,
            threadStore,
            threadWorkspace,
            settingsStore);
        persistence = new ThreadStatePersistenceUseCaseService(settingsStore, threadStore, threadWorkspace);
        turns = new TurnExecutionUseCaseService(harnesses, workspace, lifecycle, persistence);
        followUps = new FollowUpQueueUseCaseService(harnesses, workspace, settingsStore, followUpQueues);
        workspace.Changed += ForwardWorkspaceChange;
    }

    public event EventHandler<ConversationWorkspaceChangedEvent>? Changed;

    public IConversationWorkspace Workspace => workspace;

    public Task<ThreadStartUseCaseResult> StartThreadAsync(
        ThreadStartUseCaseRequest request,
        CancellationToken cancellationToken = default) =>
        lifecycle.StartAsync(request, cancellationToken);

    public Task<ThreadResumeUseCaseResult> ResumeThreadAsync(
        ThreadResumeUseCaseRequest request,
        CancellationToken cancellationToken = default) =>
        lifecycle.ResumeAsync(request, cancellationToken);

    public Task<ThreadActivationUseCaseResult> ResumeOrReplaceThreadAsync(
        ThreadActivationUseCaseRequest request,
        CancellationToken cancellationToken = default) =>
        lifecycle.ResumeOrReplaceAsync(request, cancellationToken);

    public Task<ThreadForkResult> ForkThreadAsync(
        ThreadForkRequest request,
        CancellationToken cancellationToken = default) =>
        lifecycle.ForkAsync(request, cancellationToken);

    public Task ArchiveThreadAsync(
        AppSettings settings,
        string threadId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default) =>
        lifecycle.ArchiveAsync(settings, threadId, connectionOptions, cancellationToken);

    public Task UnarchiveThreadAsync(
        AppSettings settings,
        string threadId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default) =>
        lifecycle.UnarchiveAsync(settings, threadId, connectionOptions, cancellationToken);

    public Task<bool> SetThreadPinnedAsync(
        AppSettings settings,
        string threadId,
        bool pinned,
        CancellationToken cancellationToken = default) =>
        lifecycle.SetPinnedAsync(settings, threadId, pinned, cancellationToken);

    public Task RenameThreadAsync(
        AppSettings settings,
        string threadId,
        string title,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default) =>
        lifecycle.RenameAsync(settings, threadId, title, connectionOptions, cancellationToken);

    public Task DeleteThreadAsync(
        AppSettings settings,
        string threadId,
        bool archiveFirst,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default) =>
        lifecycle.DeleteAsync(settings, threadId, archiveFirst, connectionOptions, cancellationToken);

    public Task RemoveWorktreeAsync(
        AppSettings settings,
        ProjectThreadState thread,
        string projectPath,
        CancellationToken cancellationToken = default) =>
        lifecycle.RemoveWorktreeAsync(settings, thread, projectPath, cancellationToken);

    public Task<TurnExecutionResult> StartTurnAsync(
        TurnExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        turns.StartAsync(request, cancellationToken);

    public Task<TurnEditExecutionResult> EditTurnAsync(
        TurnEditExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        turns.EditAsync(request, cancellationToken);

    public Task<ConversationWorkspaceSnapshot> SteerTurnAsync(
        string threadId,
        HarnessConnectionOptions connectionOptions,
        SteerTurnCommand command,
        string guidance,
        CancellationToken cancellationToken = default) =>
        turns.SteerAsync(threadId, connectionOptions, command, guidance, cancellationToken);

    public Task CancelTurnAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default) =>
        turns.CancelAsync(connectionOptions, address, remoteTurnId, cancellationToken);

    public Task<ThreadStateSaveResult?> SaveThreadAsync(
        AppSettings settings,
        string threadId,
        CancellationToken cancellationToken = default) =>
        persistence.SaveAsync(settings, threadId, cancellationToken);

    public Task<ThreadStateSaveResult> SaveActiveThreadAsync(
        AppSettings settings,
        ProjectThreadState? selectedThread,
        ThreadScopeKey scope,
        string threadId,
        string workspacePath,
        string title,
        CancellationToken cancellationToken = default) =>
        persistence.SaveActiveAsync(
            settings,
            selectedThread,
            scope,
            threadId,
            workspacePath,
            title,
            cancellationToken);

    public Task SaveSelectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        persistence.SaveSelectionAsync(settings, cancellationToken);

    public bool HasFollowUpQueue(string threadId) => followUps.HasQueue(threadId);

    public int GetFollowUpCount(string threadId) => followUps.GetCount(threadId);

    public IReadOnlyList<QueuedFollowUpSnapshot> GetFollowUpSnapshots(string threadId) =>
        followUps.GetSnapshots(threadId);

    public QueuedFollowUpSnapshot? GetFirstPendingFollowUp(string threadId) =>
        followUps.GetFirstPending(threadId);

    public QueuedFollowUpSnapshot? GetFollowUp(string threadId, string followUpId) =>
        followUps.Get(threadId, followUpId);

    public bool ContainsFollowUp(string threadId, string followUpId) =>
        followUps.Contains(threadId, followUpId);

    public bool IsFirstFollowUp(string threadId, string followUpId) =>
        followUps.IsFirst(threadId, followUpId);

    public Task<FollowUpQueueMutationResult> EnqueueFollowUpAsync(
        FollowUpEnqueueUseCaseRequest request,
        CancellationToken cancellationToken = default) =>
        followUps.EnqueueAsync(request, cancellationToken);

    public Task<FollowUpQueueMutationResult> ReplaceFollowUpsAsync(
        AppSettings settings,
        string threadId,
        IEnumerable<QueuedFollowUpSnapshot> snapshots,
        CancellationToken cancellationToken = default) =>
        followUps.ReplaceAsync(settings, threadId, snapshots, cancellationToken);

    public Task<FollowUpQueueMutationResult> MarkFollowUpPendingAsync(
        AppSettings settings,
        string threadId,
        string followUpId,
        CancellationToken cancellationToken = default) =>
        followUps.MarkPendingAsync(settings, threadId, followUpId, cancellationToken);

    public Task<FollowUpQueueMutationResult> SteerFollowUpAsync(
        AppSettings settings,
        string threadId,
        string followUpId,
        HarnessConnectionOptions connectionOptions,
        SteerTurnCommand command,
        CancellationToken cancellationToken = default) =>
        followUps.SteerAsync(
            settings,
            threadId,
            followUpId,
            connectionOptions,
            command,
            cancellationToken);

    public Task<FollowUpDispatchUseCaseResult> DispatchNextFollowUpAsync(
        FollowUpDispatchUseCaseRequest request,
        CancellationToken cancellationToken = default) =>
        followUps.DispatchNextAsync(request, cancellationToken);

    public Task<FollowUpQueueMutationResult> PersistFollowUpsAsync(
        AppSettings settings,
        string threadId,
        CancellationToken cancellationToken = default) =>
        followUps.PersistAsync(settings, threadId, cancellationToken);

    public Task RemoveFollowUpsAsync(string threadId) => followUps.RemoveAsync(threadId);

    public async ValueTask DisposeAsync()
    {
        workspace.Changed -= ForwardWorkspaceChange;
        await followUps.DisposeAsync().ConfigureAwait(false);
    }

    private void ForwardWorkspaceChange(object? sender, ConversationWorkspaceChangedEvent change) =>
        Changed?.Invoke(this, change);
}
