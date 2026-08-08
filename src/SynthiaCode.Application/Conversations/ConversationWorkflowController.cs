using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Application.Conversations;

/// <summary>
/// Owns non-visual conversation identity, notification routing, and detached runtime snapshots.
/// Application use-case services control lifecycle, turn execution, queue dispatch, and persistence.
/// </summary>
public sealed class ConversationWorkflowController : IConversationWorkspace
{
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly CodexFollowUpQueueWorkspace followUpQueues;
    private readonly object stateGate = new();
    private readonly HashSet<string> loadedThreadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> runningThreadIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeTurnIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastFinishedTurnIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationOperationKind> pendingOperations = new(StringComparer.Ordinal);

    public event EventHandler<ConversationWorkspaceChangedEvent>? Changed;

    public ConversationWorkflowController(
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace,
        CodexFollowUpQueueWorkspace followUpQueues)
    {
        this.threadStore = threadStore;
        this.threadWorkspace = threadWorkspace;
        this.followUpQueues = followUpQueues;
    }

    private readonly ThreadStore threadStore;

    public IReadOnlyList<ProjectThreadState> GetThreads(AppSettings settings, ThreadScopeKey scope) =>
        threadStore.GetThreads(settings, scope).Select(CloneThread).ToArray();

    public IReadOnlyList<ProjectThreadState> GetProjectThreads(AppSettings settings, string projectPath) =>
        threadStore.GetProjectThreads(settings, projectPath).Select(CloneThread).ToArray();

    public ProjectThreadState? GetActiveThread(AppSettings settings, ThreadScopeKey scope) =>
        threadStore.GetActive(settings, scope) is { } state ? CloneThread(state) : null;

    public void SetActiveThread(AppSettings settings, ThreadScopeKey scope, string threadId) => threadStore.SetActive(settings, scope, threadId);



    public void SetThreadArchived(AppSettings settings, string threadId, bool archived) => threadStore.SetArchived(settings, threadId, archived);

    public bool HasThread(string threadId)
    {
        lock (stateGate)
        {
            return threadWorkspace.ThreadIds.Contains(threadId);
        }
    }

    public ConversationWorkspaceSnapshot GetSnapshot(string? threadId)
    {
        lock (stateGate)
        {
            return GetSnapshotLocked(threadId);
        }
    }

    public ConversationWorkspaceSnapshot RestoreThread(ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (stateGate)
        {
            threadWorkspace.Restore(state);
            followUpQueues.Restore(state.ThreadId, state.QueuedFollowUps);
            return GetSnapshotLocked(state.ThreadId);
        }
    }

    public ConversationWorkspaceSnapshot ResetActiveConversation() => ConversationWorkspaceSnapshot.Empty;

    public void ReconcileHistory(string threadId, IEnumerable<CodexConversationTurnSnapshot> turns)
    {
        lock (stateGate)
        {
            threadWorkspace.GetRequired(threadId).ReconcileHistory(turns);
            MarkLoadedLocked(threadId);
        }
    }


    public CodexConversationTurnSnapshot? GetConversationTurn(string threadId, string turnId)
    {
        lock (stateGate)
        {
            return threadWorkspace.GetRequired(threadId).ConversationTurns
                .FirstOrDefault(turn => string.Equals(turn.TurnId, turnId, StringComparison.Ordinal))
                ?.ToSnapshot();
        }
    }

    public void BeginTurn(
        string threadId,
        string prompt,
        IEnumerable<AttachmentReference>? attachments = null,
        ConversationOperationKind operation = ConversationOperationKind.DirectTurn)
    {
        ConversationWorkspaceChangedEvent change;
        lock (stateGate)
        {
            pendingOperations[threadId] = operation;
            threadWorkspace.GetRequired(threadId).BeginTurn(prompt, attachments);
            change = CreateChangeLocked(
                threadId,
                ConversationWorkspaceChangeKind.PendingTurnStarted,
                operation: operation);
        }
        Changed?.Invoke(this, change);
    }

    public CodexConversationTurnSnapshot BindPendingTurn(string threadId, string turnId)
    {
        lock (stateGate)
        {
            var turn = threadWorkspace.GetRequired(threadId).BindPendingTurn(turnId);
            return turn.ToSnapshot();
        }
    }

    public void FailPendingTurn(string threadId, string detail)
    {
        ConversationWorkspaceChangedEvent change;
        lock (stateGate)
        {
            threadWorkspace.GetRequired(threadId).FailPendingTurn(detail);
            change = CreateChangeLocked(
                threadId,
                ConversationWorkspaceChangeKind.PendingTurnFailed,
                operation: GetPendingOperationLocked(threadId));
        }
        Changed?.Invoke(this, change);
    }

    public void AddGuidance(string threadId, string guidance)
    {
        lock (stateGate)
        {
            threadWorkspace.GetRequired(threadId).AddGuidance(guidance);
        }
    }

    public int GetActiveRollbackTurnCount(string threadId, string turnId)
    {
        lock (stateGate)
        {
            return threadWorkspace.GetRequired(threadId).GetActiveRollbackTurnCount(
                threadWorkspace.GetRequired(threadId).ConversationTurns.First(turn => turn.TurnId == turnId));
        }
    }

    public void SupersedeTurnsFrom(string threadId, string turnId)
    {
        lock (stateGate)
        {
            threadWorkspace.GetRequired(threadId).SupersedeTurnsFrom(
                threadWorkspace.GetRequired(threadId).ConversationTurns.First(turn => turn.TurnId == turnId));
        }
    }

    public void RegisterTurn(string threadId, string turnId)
    {
        lock (stateGate)
        {
            threadWorkspace.RegisterTurn(threadId, turnId);
        }
    }


    private string? activeThreadId;
    private string? activeTurnId;
    private bool activeThreadLoaded;

    public string? ActiveThreadId
    {
        get { lock (stateGate) return activeThreadId; }
    }

    public string? ActiveTurnId
    {
        get { lock (stateGate) return activeTurnId; }
    }

    public bool ActiveThreadLoaded
    {
        get { lock (stateGate) return activeThreadLoaded; }
    }

    public void Select(string? threadId)
    {
        lock (stateGate)
        {
            SelectLocked(threadId);
        }
    }

    public void SetActiveThreadLoaded(bool loaded)
    {
        lock (stateGate) activeThreadLoaded = loaded;
    }

    public void SetActiveTurn(string? turnId)
    {
        lock (stateGate) activeTurnId = turnId;
    }

    public bool IsLoaded(string threadId)
    {
        lock (stateGate) return loadedThreadIds.Contains(threadId);
    }

    public bool IsRunning(string threadId)
    {
        lock (stateGate) return runningThreadIds.Contains(threadId);
    }

    public int ActiveTurnCount
    {
        get { lock (stateGate) return activeTurnIds.Count; }
    }

    public bool TryGetActiveTurn(string threadId, out string turnId)
    {
        lock (stateGate) return activeTurnIds.TryGetValue(threadId, out turnId!);
    }

    public IReadOnlyList<KeyValuePair<string, string>> SnapshotActiveTurns()
    {
        lock (stateGate) return activeTurnIds.ToArray();
    }

    public IReadOnlyList<string> SnapshotRunningThreadIds()
    {
        lock (stateGate) return runningThreadIds.ToArray();
    }

    public void MarkLoaded(string threadId)
    {
        lock (stateGate)
        {
            MarkLoadedLocked(threadId);
        }
    }

    public void ClearRuntimeState()
    {
        lock (stateGate)
        {
            runningThreadIds.Clear();
            activeTurnIds.Clear();
            lastFinishedTurnIds.Clear();
            loadedThreadIds.Clear();
            pendingOperations.Clear();
            activeTurnId = null;
            activeThreadLoaded = false;
        }
    }

    public void RegisterCreated(ProjectThreadState state)
    {
        lock (stateGate)
        {
            threadWorkspace.Restore(state);
            followUpQueues.Restore(state.ThreadId, state.QueuedFollowUps);
            loadedThreadIds.Add(state.ThreadId);
            SelectLocked(state.ThreadId);
        }
    }

    public void RegisterResumed(string threadId, IReadOnlyList<CodexConversationTurnSnapshot> turns)
    {
        lock (stateGate)
        {
            var service = threadWorkspace.GetRequired(threadId);
            service.ReconcileHistory(turns);
            loadedThreadIds.Add(threadId);
            if (string.Equals(activeThreadId, threadId, StringComparison.Ordinal))
            {
                activeThreadLoaded = true;
            }
        }
    }

    public void RegisterTurnStarted(string threadId, string turnId, CodexTurnStatus status)
    {
        ConversationWorkspaceChangedEvent change;
        lock (stateGate)
        {
            change = RegisterTurnStartedLocked(threadId, turnId, status);
        }
        Changed?.Invoke(this, change);
    }

    public void RegisterTurnFinished(string threadId)
    {
        lock (stateGate)
        {
            RegisterTurnFinishedLocked(threadId);
        }
    }

    public ConversationNotificationResult ApplyThreadNotification(CodexAppServerNotification notification)
    {
        lock (stateGate)
        {
            var threadId = threadWorkspace.ApplyNotification(notification);
            threadId ??= activeThreadId;
            if (string.IsNullOrWhiteSpace(threadId) || !threadWorkspace.ThreadIds.Contains(threadId))
            {
                return ConversationNotificationResult.Unrouted;
            }

            var service = threadWorkspace.GetRequired(threadId);
            if (!string.IsNullOrWhiteSpace(service.ActiveTurnId))
            {
                activeTurnIds[threadId] = service.ActiveTurnId;
                threadWorkspace.RegisterTurn(threadId, service.ActiveTurnId);
            }
            if (notification.Kind == CodexAppServerNotificationKind.TurnCompleted)
            {
                RegisterTurnFinishedLocked(threadId, notification.TurnId);
            }
            return new ConversationNotificationResult(
                threadId,
                GetSnapshotLocked(threadId),
                notification.Kind == CodexAppServerNotificationKind.TurnCompleted,
                notification.IsArchived);
        }
    }

    public ConversationNotificationResult ApplyHarnessEvent(HarnessEvent harnessEvent)
    {
        ArgumentNullException.ThrowIfNull(harnessEvent);
        ConversationWorkspaceChangedEvent? change = null;
        ConversationNotificationResult result;
        lock (stateGate)
        {
            var threadId = threadWorkspace.ApplyEvent(harnessEvent);
            threadId ??= activeThreadId;
            if (string.IsNullOrWhiteSpace(threadId) || !threadWorkspace.ThreadIds.Contains(threadId))
            {
                return ConversationNotificationResult.Unrouted;
            }

            var service = threadWorkspace.GetRequired(threadId);
            if (!string.IsNullOrWhiteSpace(service.ActiveTurnId))
            {
                activeTurnIds[threadId] = service.ActiveTurnId;
                threadWorkspace.RegisterTurn(threadId, service.ActiveTurnId);
            }
            if (harnessEvent is TurnStartedEvent started)
            {
                change = RegisterTurnStartedLocked(threadId, started.RemoteTurnId!, CodexTurnStatus.Running);
            }
            if (harnessEvent is TurnCompletedEvent)
            {
                RegisterTurnFinishedLocked(threadId, harnessEvent.RemoteTurnId);
            }

            result = new ConversationNotificationResult(
                threadId,
                GetSnapshotLocked(threadId),
                harnessEvent is TurnCompletedEvent,
                harnessEvent is ConversationArchivedEvent archived ? archived.IsArchived : null);
        }
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
        return result;
    }

    public void RemoveRuntime(string threadId)
    {
        lock (stateGate)
        {
            loadedThreadIds.Remove(threadId);
            runningThreadIds.Remove(threadId);
            activeTurnIds.Remove(threadId);
            lastFinishedTurnIds.Remove(threadId);
            pendingOperations.Remove(threadId);
            threadWorkspace.Remove(threadId);
            if (string.Equals(activeThreadId, threadId, StringComparison.Ordinal))
            {
                SelectLocked(null);
            }
        }
    }

    private ConversationWorkspaceChangedEvent RegisterTurnStartedLocked(
        string threadId,
        string turnId,
        CodexTurnStatus status)
    {
        var operation = GetPendingOperationLocked(threadId);
        if (status == CodexTurnStatus.Running &&
            lastFinishedTurnIds.TryGetValue(threadId, out var finishedTurnId) &&
            string.Equals(finishedTurnId, turnId, StringComparison.Ordinal))
        {
            status = GetSnapshotLocked(threadId).ActiveTurnStatus;
            if (status == CodexTurnStatus.Running)
            {
                status = CodexTurnStatus.Completed;
            }
        }
        if (status == CodexTurnStatus.Running)
        {
            runningThreadIds.Add(threadId);
            activeTurnIds[threadId] = turnId;
        }
        else
        {
            runningThreadIds.Remove(threadId);
            activeTurnIds.Remove(threadId);
        }
        if (string.Equals(activeThreadId, threadId, StringComparison.Ordinal))
        {
            activeTurnId = status == CodexTurnStatus.Running ? turnId : null;
        }
        return CreateChangeLocked(
            threadId,
            ConversationWorkspaceChangeKind.TurnStarted,
            turnId,
            status,
            operation);
    }

    private void RegisterTurnFinishedLocked(string threadId, string? turnId = null)
    {
        turnId ??= activeTurnIds.TryGetValue(threadId, out var activeTurnIdValue)
            ? activeTurnIdValue
            : null;
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            lastFinishedTurnIds[threadId] = turnId;
        }
        runningThreadIds.Remove(threadId);
        activeTurnIds.Remove(threadId);
        pendingOperations.Remove(threadId);
        if (string.Equals(activeThreadId, threadId, StringComparison.Ordinal))
        {
            activeTurnId = null;
        }
    }

    private void SelectLocked(string? threadId)
    {
        activeThreadId = threadId;
        activeTurnId = threadId is not null && activeTurnIds.TryGetValue(threadId, out var turnId) ? turnId : null;
        activeThreadLoaded = threadId is not null && loadedThreadIds.Contains(threadId);
    }

    private void MarkLoadedLocked(string threadId)
    {
        loadedThreadIds.Add(threadId);
        if (string.Equals(activeThreadId, threadId, StringComparison.Ordinal))
        {
            activeThreadLoaded = true;
        }
    }

    private ConversationWorkspaceChangedEvent CreateChangeLocked(
        string threadId,
        ConversationWorkspaceChangeKind kind,
        string? turnId = null,
        CodexTurnStatus? turnStatus = null,
        ConversationOperationKind operation = ConversationOperationKind.External) => new(
            threadId,
            kind,
            GetSnapshotLocked(threadId),
            turnId,
            turnStatus,
            operation);

    private ConversationWorkspaceSnapshot GetSnapshotLocked(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !threadWorkspace.ThreadIds.Contains(threadId))
        {
            return ConversationWorkspaceSnapshot.Empty;
        }

        var service = threadWorkspace.GetRequired(threadId);
        var queue = followUpQueues.ThreadIds.Contains(threadId)
            ? followUpQueues.GetRequired(threadId)
            : null;
        return ConversationWorkspaceSnapshot.Create(threadId, service, queue);
    }

    private ConversationOperationKind GetPendingOperationLocked(string threadId) =>
        pendingOperations.TryGetValue(threadId, out var operation)
            ? operation
            : ConversationOperationKind.External;


    private static ProjectThreadState CloneThread(ProjectThreadState source) =>
        SettingsStorageMapper.ToPresentation(SettingsStorageMapper.ToPersisted(source));
}

public sealed record ConversationNotificationResult(
    string? ThreadId,
    ConversationWorkspaceSnapshot Snapshot,
    bool IsTurnCompleted,
    bool? IsArchived)
{
    public static ConversationNotificationResult Unrouted { get; } = new(
        null,
        ConversationWorkspaceSnapshot.Empty,
        false,
        null);
}


/// <summary>
/// A detached, read-only conversation projection for the presentation layer.  It never
/// exposes the mutable runtime service or queue owned by <see cref="ConversationWorkflowController"/>.
/// </summary>
public sealed record ConversationWorkspaceSnapshot(
    string? ThreadId,
    string? ActiveTurnId,
    CodexTurnStatus ActiveTurnStatus,
    string FinalResponse,
    bool RequiresAuthentication,
    long ContextTokensUsed,
    long ContextWindowTokens,
    int ContextCompactionCount,
    IReadOnlyList<CodexTimelineItem> TimelineItems,
    IReadOnlyList<string> RawEvents,
    IReadOnlyList<CodexConversationTurnSnapshot> ConversationTurns,
    IReadOnlyList<QueuedFollowUpSnapshot> QueuedFollowUps)
{
    public static ConversationWorkspaceSnapshot Empty { get; } = new(
        null, null, CodexTurnStatus.Idle, string.Empty, false, 0, 0, 0, [], [], [], []);

    internal static ConversationWorkspaceSnapshot Create(
        string threadId,
        CodexThreadService service,
        CodexFollowUpQueue? queue) => new(
            threadId,
            service.ActiveTurnId,
            service.ActiveTurnStatus,
            service.FinalResponse,
            service.RequiresAuthentication,
            service.ContextTokensUsed,
            service.ContextWindowTokens,
            service.ContextCompactionCount,
            service.TimelineItems.Select(item => item with { }).ToArray(),
            service.RawEvents.ToArray(),
            service.SnapshotConversation().Select(CloneTurn).ToArray(),
            queue?.Snapshot().Select(item => item.Clone()).ToArray() ?? []);

    private static CodexConversationTurnSnapshot CloneTurn(CodexConversationTurnSnapshot source) => new()
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
        Activity = [.. source.Activity.Select(item => item with { })],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. source.GeneratedImagePaths],
        Diff = source.Diff
    };
}
