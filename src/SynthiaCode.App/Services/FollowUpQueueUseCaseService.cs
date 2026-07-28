using System.IO;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.Services;

/// <summary>
/// Owns queued follow-up mutation, durable snapshots, steering, and serialized dispatch.
/// The shell supplies request composition because model capability selection is presentation state.
/// </summary>
public sealed class FollowUpQueueUseCaseService : IAsyncDisposable
{
    private readonly IAppServerSessionCoordinator appServer;
    private readonly ConversationWorkflowController conversations;
    private readonly ISettingsStore settingsStore;
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly CodexFollowUpQueueWorkspace queues;
    private readonly object dispatchSync = new();
    private readonly Dictionary<string, FollowUpDispatchOperation> dispatchOperations = new(StringComparer.Ordinal);
    private bool disposing;
    private Task? disposeTask;

    public FollowUpQueueUseCaseService(
        IAppServerSessionCoordinator appServer,
        ConversationWorkflowController conversations,
        ISettingsStore settingsStore,
        CodexThreadWorkspace threadWorkspace,
        CodexFollowUpQueueWorkspace queues)
    {
        this.appServer = appServer;
        this.conversations = conversations;
        this.settingsStore = settingsStore;
        this.threadWorkspace = threadWorkspace;
        this.queues = queues;
    }

    public bool HasQueue(string threadId) => queues.ThreadIds.Contains(threadId);

    public IReadOnlyList<QueuedFollowUpSnapshot> GetSnapshots(string threadId) =>
        !queues.ThreadIds.Contains(threadId)
            ? []
            : queues.GetRequired(threadId).Snapshot().Select(item => item.Clone()).ToArray();

    public QueuedFollowUpSnapshot? GetFirstPending(string threadId) =>
        !queues.ThreadIds.Contains(threadId)
            ? null
            : queues.GetRequired(threadId).Items
                .FirstOrDefault(item => item.State == QueuedFollowUpState.Pending)?.Snapshot();

    public int GetCount(string threadId) =>
        queues.ThreadIds.Contains(threadId) ? queues.GetRequired(threadId).Items.Count : 0;

    public bool Contains(string threadId, string followUpId) =>
        queues.ThreadIds.Contains(threadId) && queues.GetRequired(threadId).IndexOf(followUpId) >= 0;

    public bool IsFirst(string threadId, string followUpId) =>
        queues.ThreadIds.Contains(threadId) &&
        string.Equals(queues.GetRequired(threadId).Items.FirstOrDefault()?.Id, followUpId, StringComparison.Ordinal);

    public QueuedFollowUpSnapshot? Get(string threadId, string followUpId) =>
        !queues.ThreadIds.Contains(threadId)
            ? null
            : queues.GetRequired(threadId).Items
                .FirstOrDefault(item => item.Id == followUpId)?.Snapshot();

    public async Task<FollowUpQueueMutationResult> EnqueueAsync(
        FollowUpEnqueueUseCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        queues.GetOrCreate(request.ThreadId).Enqueue(
            request.Text,
            request.Options,
            request.Attachments,
            request.SkillInputs);
        return await PersistAsync(request.Settings, request.ThreadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpQueueMutationResult> ReplaceAsync(
        AppSettings settings,
        string threadId,
        IEnumerable<QueuedFollowUpSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        queues.GetOrCreate(threadId).Restore(snapshots);
        return await PersistAsync(settings, threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpQueueMutationResult> MarkPendingAsync(
        AppSettings settings,
        string threadId,
        string followUpId,
        CancellationToken cancellationToken = default)
    {
        queues.GetRequired(threadId).MarkPending(followUpId);
        return await PersistAsync(settings, threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpQueueMutationResult> SteerAsync(
        AppSettings settings,
        string threadId,
        string followUpId,
        CodexTurnSteerRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = Get(threadId, followUpId)
            ?? throw new InvalidOperationException("The queued follow-up is no longer available.");
        await appServer.SteerTurnAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            conversations.AddGuidance(threadId, item.Text);
        }
        queues.GetRequired(threadId).Remove(followUpId);
        return await PersistAsync(settings, threadId, cancellationToken).ConfigureAwait(false);
    }

    public Task<FollowUpDispatchUseCaseResult> DispatchNextAsync(
        FollowUpDispatchUseCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (conversations.IsRunning(request.ThreadId) || !queues.ThreadIds.Contains(request.ThreadId))
        {
            return Task.FromResult(new FollowUpDispatchUseCaseResult(
                QueuedFollowUpDispatchResult.NotStarted,
                conversations.GetSnapshot(request.ThreadId)));
        }

        FollowUpDispatchOperation operation;
        try
        {
            operation = GetOrCreateDispatchOperation(request.ThreadId);
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(new FollowUpDispatchUseCaseResult(
                QueuedFollowUpDispatchResult.NotStarted,
                conversations.GetSnapshot(request.ThreadId)));
        }

        var completion = new TaskCompletionSource<FollowUpDispatchUseCaseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (dispatchSync)
        {
            if (disposing || operation.Removing)
            {
                return Task.FromResult(new FollowUpDispatchUseCaseResult(
                    QueuedFollowUpDispatchResult.NotStarted,
                    conversations.GetSnapshot(request.ThreadId)));
            }
            operation.InFlight.Add(completion.Task);
        }
        _ = CompleteDispatchAsync(operation, completion, request, cancellationToken);
        return completion.Task;
    }

    public async Task<FollowUpQueueMutationResult> PersistAsync(
        AppSettings settings,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (!queues.ThreadIds.Contains(threadId))
        {
            return FollowUpQueueMutationResult.NotFound;
        }
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted is null)
        {
            return FollowUpQueueMutationResult.NotFound;
        }
        var snapshots = queues.GetRequired(threadId).Snapshot().Select(item => item.Clone()).ToList();
        persisted.QueuedFollowUps = snapshots;
        persisted.UpdatedAt = DateTimeOffset.UtcNow;
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return new FollowUpQueueMutationResult(
            true,
            snapshots.Select(item => item.Clone()).ToArray(),
            persisted.UpdatedAt,
            conversations.GetSnapshot(threadId));
    }

    public async Task RemoveAsync(string threadId)
    {
        FollowUpDispatchOperation? operation;
        Task[] inFlight;
        lock (dispatchSync)
        {
            dispatchOperations.TryGetValue(threadId, out operation);
            if (operation is not null)
            {
                operation.Removing = true;
                operation.Cancellation.Cancel();
                inFlight = operation.InFlight.ToArray();
            }
            else
            {
                inFlight = [];
            }
        }
        if (inFlight.Length > 0)
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        lock (dispatchSync)
        {
            if (operation is not null)
            {
                dispatchOperations.Remove(threadId);
                operation.Dispose();
            }
        }
        queues.Remove(threadId);
    }

    public ValueTask DisposeAsync()
    {
        lock (dispatchSync)
        {
            if (disposeTask is not null)
            {
                return new ValueTask(disposeTask);
            }
            disposing = true;
            var operations = dispatchOperations.Values.ToArray();
            foreach (var operation in operations)
            {
                operation.Removing = true;
                operation.Cancellation.Cancel();
            }
            disposeTask = DisposeOperationsAsync(operations);
            return new ValueTask(disposeTask);
        }
    }

    private FollowUpDispatchOperation GetOrCreateDispatchOperation(string threadId)
    {
        lock (dispatchSync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (!dispatchOperations.TryGetValue(threadId, out var operation))
            {
                operation = new FollowUpDispatchOperation();
                dispatchOperations.Add(threadId, operation);
            }
            return operation;
        }
    }

    private async Task CompleteDispatchAsync(
        FollowUpDispatchOperation operation,
        TaskCompletionSource<FollowUpDispatchUseCaseResult> completion,
        FollowUpDispatchUseCaseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operation.Cancellation.Token);
            var dispatch = await DispatchNextCoreAsync(
                operation,
                request,
                linkedCancellation.Token).ConfigureAwait(false);
            completion.TrySetResult(new FollowUpDispatchUseCaseResult(
                dispatch,
                conversations.GetSnapshot(request.ThreadId)));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult(new FollowUpDispatchUseCaseResult(
                QueuedFollowUpDispatchResult.Cancelled,
                conversations.GetSnapshot(request.ThreadId)));
        }
        catch (Exception ex)
        {
            completion.TrySetResult(new FollowUpDispatchUseCaseResult(
                QueuedFollowUpDispatchResult.UnexpectedFailure(ex.Message),
                conversations.GetSnapshot(request.ThreadId)));
        }
        finally
        {
            lock (dispatchSync)
            {
                operation.InFlight.Remove(completion.Task);
            }
        }
    }

    private async Task<QueuedFollowUpDispatchResult> DispatchNextCoreAsync(
        FollowUpDispatchOperation operation,
        FollowUpDispatchUseCaseRequest request,
        CancellationToken cancellationToken)
    {
        await operation.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operation.Removing ||
                conversations.IsRunning(request.ThreadId) ||
                !queues.ThreadIds.Contains(request.ThreadId) ||
                queues.GetRequired(request.ThreadId).Items.FirstOrDefault() is not
                    { State: QueuedFollowUpState.Pending } item)
            {
                return QueuedFollowUpDispatchResult.NotStarted;
            }

            var queue = queues.GetRequired(request.ThreadId);
            var service = threadWorkspace.GetRequired(request.ThreadId);
            var snapshot = item.Snapshot();
            var originalIndex = queue.IndexOf(item.Id);
            queue.MarkStarting(snapshot.Id);

            try
            {
                await PersistAsync(request.Settings, request.ThreadId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await MarkNeedsAttentionAndPersistAsync(
                    request,
                    queue,
                    snapshot.Id,
                    "The follow-up could not be marked as starting: " + ex.Message,
                    wasRemoteStarted: false,
                    turnId: null,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                service.BeginTurn(snapshot.Text, snapshot.Attachments);
                await request.EnsureConnected(cancellationToken).ConfigureAwait(false);
                var workspacePath = Path.GetFullPath(snapshot.Options.WorkspacePath);
                if (!Directory.Exists(workspacePath))
                {
                    throw new InvalidOperationException(
                        $"The queued follow-up workspace is unavailable: {workspacePath}");
                }
                var persistedThread = request.Settings.ProjectThreads.FirstOrDefault(thread =>
                    string.Equals(thread.ThreadId, request.ThreadId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"Thread '{request.ThreadId}' is no longer available.");
                persistedThread.Preview = snapshot.Text;
                var started = await appServer.StartTurnAsync(
                    request.CreateStartRequest(snapshot.Clone()),
                    cancellationToken).ConfigureAwait(false);
                var bound = service.BindPendingTurn(started.TurnId);
                threadWorkspace.RegisterTurn(request.ThreadId, started.TurnId);
                conversations.RegisterTurnStarted(request.ThreadId, started.TurnId, bound.Status);

                try
                {
                    await PersistAsync(request.Settings, request.ThreadId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return await MarkNeedsAttentionAndPersistAsync(
                        request,
                        queue,
                        snapshot.Id,
                        "The follow-up started remotely but its state could not be saved: " + ex.Message,
                        wasRemoteStarted: true,
                        turnId: started.TurnId,
                        cancellationToken).ConfigureAwait(false);
                }

                queue.Remove(snapshot.Id);
                try
                {
                    await PersistAsync(request.Settings, request.ThreadId, cancellationToken).ConfigureAwait(false);
                    return QueuedFollowUpDispatchResult.Started(snapshot.Id, started.TurnId, bound.Status);
                }
                catch (Exception ex)
                {
                    var restore = snapshot.Clone();
                    restore.State = QueuedFollowUpState.NeedsAttention;
                    restore.LastError =
                        "The follow-up started remotely but queue removal could not be saved: " + ex.Message;
                    restore.UpdatedAt = DateTimeOffset.UtcNow;
                    queue.RestoreAt(originalIndex, restore);
                    return await PersistRestoredAttentionAsync(
                        request,
                        snapshot.Id,
                        restore.LastError,
                        started.TurnId,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                service.FailPendingTurn("Queued follow-up start was cancelled.");
                return await MarkNeedsAttentionAndPersistAsync(
                    request,
                    queue,
                    snapshot.Id,
                    "Queued follow-up start was cancelled.",
                    wasRemoteStarted: false,
                    turnId: null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                service.FailPendingTurn(ex.Message);
                return await MarkNeedsAttentionAndPersistAsync(
                    request,
                    queue,
                    snapshot.Id,
                    ex.Message,
                    wasRemoteStarted: false,
                    turnId: null,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            operation.Gate.Release();
        }
    }

    private async Task<QueuedFollowUpDispatchResult> MarkNeedsAttentionAndPersistAsync(
        FollowUpDispatchUseCaseRequest request,
        CodexFollowUpQueue queue,
        string followUpId,
        string error,
        bool wasRemoteStarted,
        string? turnId,
        CancellationToken cancellationToken)
    {
        if (queue.IndexOf(followUpId) >= 0)
        {
            queue.MarkNeedsAttention(followUpId, error);
        }
        return await PersistRestoredAttentionAsync(
            request,
            followUpId,
            error,
            turnId,
            cancellationToken,
            wasRemoteStarted).ConfigureAwait(false);
    }

    private async Task<QueuedFollowUpDispatchResult> PersistRestoredAttentionAsync(
        FollowUpDispatchUseCaseRequest request,
        string followUpId,
        string? error,
        string? turnId,
        CancellationToken cancellationToken,
        bool wasRemoteStarted = true)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(request.Settings, request.ThreadId, cancellationToken).ConfigureAwait(false);
            return QueuedFollowUpDispatchResult.NeedsAttention(
                followUpId,
                turnId,
                error,
                wasRemoteStarted);
        }
        catch (Exception persistenceException)
        {
            var combined = string.IsNullOrWhiteSpace(error)
                ? persistenceException.Message
                : error + " Queue recovery could not be saved: " + persistenceException.Message;
            return QueuedFollowUpDispatchResult.NeedsAttention(
                followUpId,
                turnId,
                combined,
                wasRemoteStarted);
        }
    }

    private async Task DisposeOperationsAsync(FollowUpDispatchOperation[] operations)
    {
        await Task.WhenAll(operations.SelectMany(operation => operation.InFlight).ToArray())
            .ConfigureAwait(false);
        foreach (var operation in operations)
        {
            operation.Dispose();
        }
        lock (dispatchSync)
        {
            dispatchOperations.Clear();
        }
    }
}

public sealed record FollowUpEnqueueUseCaseRequest(
    AppSettings Settings,
    string ThreadId,
    string Text,
    QueuedTurnOptionsSnapshot Options,
    IReadOnlyList<AttachmentReference> Attachments,
    IReadOnlyList<CodexSkillInput> SkillInputs);

public sealed record FollowUpDispatchUseCaseRequest(
    AppSettings Settings,
    string ThreadId,
    Func<QueuedFollowUpSnapshot, CodexTurnStartRequest> CreateStartRequest,
    Func<CancellationToken, Task> EnsureConnected);

public sealed record QueuedFollowUpDispatchResult(
    bool Attempted,
    string? FollowUpId,
    string? TurnId,
    CodexTurnStatus? TurnStatus,
    QueuedFollowUpState? FollowUpState,
    string? ErrorMessage,
    bool RemoteTurnStarted)
{
    public static QueuedFollowUpDispatchResult NotStarted { get; } =
        new(false, null, null, null, null, null, false);
    public static QueuedFollowUpDispatchResult Cancelled { get; } =
        new(true, null, null, null, QueuedFollowUpState.NeedsAttention,
            "Queued follow-up start was cancelled.", false);
    public static QueuedFollowUpDispatchResult Started(
        string followUpId,
        string turnId,
        CodexTurnStatus status) =>
        new(true, followUpId, turnId, status, null, null, true);
    public static QueuedFollowUpDispatchResult NeedsAttention(
        string followUpId,
        string? turnId,
        string? error,
        bool remoteTurnStarted) =>
        new(true, followUpId, turnId, null, QueuedFollowUpState.NeedsAttention, error, remoteTurnStarted);
    public static QueuedFollowUpDispatchResult UnexpectedFailure(string error) =>
        new(true, null, null, null, QueuedFollowUpState.NeedsAttention, error, false);
}

public sealed record FollowUpDispatchUseCaseResult(
    QueuedFollowUpDispatchResult Dispatch,
    ConversationWorkspaceSnapshot Snapshot);

public sealed record FollowUpQueueMutationResult(
    bool Found,
    IReadOnlyList<QueuedFollowUpSnapshot> Snapshots,
    DateTimeOffset UpdatedAt,
    ConversationWorkspaceSnapshot Snapshot)
{
    public static FollowUpQueueMutationResult NotFound { get; } = new(
        false,
        [],
        default,
        ConversationWorkspaceSnapshot.Empty);
}

internal sealed class FollowUpDispatchOperation : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CancellationTokenSource Cancellation { get; } = new();
    public HashSet<Task> InFlight { get; } = [];
    public bool Removing { get; set; }

    public void Dispose()
    {
        Cancellation.Dispose();
        Gate.Dispose();
    }
}
