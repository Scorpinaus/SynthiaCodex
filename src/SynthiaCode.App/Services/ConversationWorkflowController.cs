using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.Services;

/// <summary>
/// Owns non-visual conversation identity, notification routing, and detached runtime snapshots.
/// Application use-case services control lifecycle, turn execution, queue dispatch, and persistence.
/// </summary>
public sealed class ConversationWorkflowController
{
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly CodexFollowUpQueueWorkspace followUpQueues;
    private readonly HashSet<string> loadedThreadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> runningThreadIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeTurnIds = new(StringComparer.Ordinal);

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

    public bool HasThread(string threadId) => threadWorkspace.ThreadIds.Contains(threadId);

    public ConversationWorkspaceSnapshot GetSnapshot(string? threadId)
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

    public ConversationWorkspaceSnapshot RestoreThread(ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        threadWorkspace.Restore(state);
        followUpQueues.Restore(state.ThreadId, state.QueuedFollowUps);
        return GetSnapshot(state.ThreadId);
    }

    public ConversationWorkspaceSnapshot ResetActiveConversation() => ConversationWorkspaceSnapshot.Empty;

    public void ReconcileHistory(string threadId, IEnumerable<CodexConversationTurnSnapshot> turns)
    {
        threadWorkspace.GetRequired(threadId).ReconcileHistory(turns);
        MarkLoaded(threadId);
    }


    public CodexConversationTurnSnapshot? GetConversationTurn(string threadId, string turnId) =>
        threadWorkspace.GetRequired(threadId).ConversationTurns
            .FirstOrDefault(turn => string.Equals(turn.TurnId, turnId, StringComparison.Ordinal))
            ?.ToSnapshot();

    public void BeginTurn(string threadId, string prompt, IEnumerable<AttachmentReference>? attachments = null) =>
        threadWorkspace.GetRequired(threadId).BeginTurn(prompt, attachments);

    public CodexConversationTurnSnapshot BindPendingTurn(string threadId, string turnId)
    {
        var turn = threadWorkspace.GetRequired(threadId).BindPendingTurn(turnId);
        return turn.ToSnapshot();
    }

    public void FailPendingTurn(string threadId, string detail) =>
        threadWorkspace.GetRequired(threadId).FailPendingTurn(detail);

    public void AddGuidance(string threadId, string guidance) =>
        threadWorkspace.GetRequired(threadId).AddGuidance(guidance);

    public int GetActiveRollbackTurnCount(string threadId, string turnId) =>
        threadWorkspace.GetRequired(threadId).GetActiveRollbackTurnCount(
            threadWorkspace.GetRequired(threadId).ConversationTurns.First(turn => turn.TurnId == turnId));

    public void SupersedeTurnsFrom(string threadId, string turnId) =>
        threadWorkspace.GetRequired(threadId).SupersedeTurnsFrom(
            threadWorkspace.GetRequired(threadId).ConversationTurns.First(turn => turn.TurnId == turnId));

    public void RegisterTurn(string threadId, string turnId) => threadWorkspace.RegisterTurn(threadId, turnId);


    public string? ActiveThreadId { get; private set; }
    public string? ActiveTurnId { get; private set; }
    public bool ActiveThreadLoaded { get; private set; }

    public void Select(string? threadId)
    {
        ActiveThreadId = threadId;
        ActiveTurnId = threadId is not null && activeTurnIds.TryGetValue(threadId, out var turnId) ? turnId : null;
        ActiveThreadLoaded = threadId is not null && loadedThreadIds.Contains(threadId);
    }

    public void SetActiveThreadLoaded(bool loaded) => ActiveThreadLoaded = loaded;

    public void SetActiveTurn(string? turnId) => ActiveTurnId = turnId;

    public bool IsLoaded(string threadId) => loadedThreadIds.Contains(threadId);

    public bool IsRunning(string threadId) => runningThreadIds.Contains(threadId);

    public int ActiveTurnCount => activeTurnIds.Count;

    public bool TryGetActiveTurn(string threadId, out string turnId) => activeTurnIds.TryGetValue(threadId, out turnId!);

    public IReadOnlyList<KeyValuePair<string, string>> SnapshotActiveTurns() => activeTurnIds.ToArray();

    public IReadOnlyList<string> SnapshotRunningThreadIds() => runningThreadIds.ToArray();

    public void MarkLoaded(string threadId)
    {
        loadedThreadIds.Add(threadId);
        if (string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
        {
            ActiveThreadLoaded = true;
        }
    }

    public void ClearRuntimeState()
    {
        runningThreadIds.Clear();
        activeTurnIds.Clear();
        loadedThreadIds.Clear();
        ActiveTurnId = null;
        ActiveThreadLoaded = false;
    }

    public void RegisterCreated(ProjectThreadState state)
    {
        threadWorkspace.Restore(state);
        followUpQueues.Restore(state.ThreadId, state.QueuedFollowUps);
        loadedThreadIds.Add(state.ThreadId);
        Select(state.ThreadId);
    }

    public void RegisterResumed(string threadId, IReadOnlyList<CodexConversationTurnSnapshot> turns)
    {
        var service = threadWorkspace.GetRequired(threadId);
        service.ReconcileHistory(turns);
        loadedThreadIds.Add(threadId);
        if (string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
        {
            ActiveThreadLoaded = true;
        }
    }

    public void RegisterTurnStarted(string threadId, string turnId, CodexTurnStatus status)
    {
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
        if (string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
        {
            ActiveTurnId = status == CodexTurnStatus.Running ? turnId : null;
        }
    }

    public void RegisterTurnFinished(string threadId)
    {
        runningThreadIds.Remove(threadId);
        activeTurnIds.Remove(threadId);
        if (string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
        {
            ActiveTurnId = null;
        }
    }

    public ConversationNotificationResult ApplyThreadNotification(CodexAppServerNotification notification)
    {
        var threadId = threadWorkspace.ApplyNotification(notification);
        threadId ??= ActiveThreadId;
        if (string.IsNullOrWhiteSpace(threadId) || !threadWorkspace.ThreadIds.Contains(threadId))
        {
            return ConversationNotificationResult.Unrouted;
        }

        var service = threadWorkspace.GetRequired(threadId);
        if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(service.ActiveTurnId))
        {
            activeTurnIds[threadId] = service.ActiveTurnId;
            threadWorkspace.RegisterTurn(threadId, service.ActiveTurnId);
        }
        if (notification.Kind == CodexAppServerNotificationKind.TurnCompleted && !string.IsNullOrWhiteSpace(threadId))
        {
            RegisterTurnFinished(threadId);
        }
        return new ConversationNotificationResult(
            threadId,
            ConversationWorkspaceSnapshot.Create(threadId, service,
                followUpQueues.ThreadIds.Contains(threadId) ? followUpQueues.GetRequired(threadId) : null),
            notification.Kind == CodexAppServerNotificationKind.TurnCompleted,
            notification.IsArchived);
    }

    public ConversationNotificationResult ApplyHarnessEvent(HarnessEvent harnessEvent)
    {
        ArgumentNullException.ThrowIfNull(harnessEvent);
        var threadId = threadWorkspace.ApplyEvent(harnessEvent);
        threadId ??= ActiveThreadId;
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
            RegisterTurnStarted(threadId, started.RemoteTurnId!, CodexTurnStatus.Running);
        }
        if (harnessEvent is TurnCompletedEvent)
        {
            RegisterTurnFinished(threadId);
        }

        return new ConversationNotificationResult(
            threadId,
            ConversationWorkspaceSnapshot.Create(
                threadId,
                service,
                followUpQueues.ThreadIds.Contains(threadId) ? followUpQueues.GetRequired(threadId) : null),
            harnessEvent is TurnCompletedEvent,
            harnessEvent is ConversationArchivedEvent archived ? archived.IsArchived : null);
    }

    public void RemoveRuntime(string threadId)
    {
        loadedThreadIds.Remove(threadId);
        runningThreadIds.Remove(threadId);
        activeTurnIds.Remove(threadId);
        threadWorkspace.Remove(threadId);
        if (string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
        {
            Select(null);
        }
    }


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
        GeneratedImagePaths = [.. source.GeneratedImagePaths]
    };
}
