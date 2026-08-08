using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Application.Conversations;

/// <summary>
/// Owns mutable conversation runtime state. Consumers receive detached snapshots and
/// application events rather than references to reducers or queues.
/// </summary>
public interface IConversationWorkspace
{
    event EventHandler<ConversationWorkspaceChangedEvent>? Changed;

    string? ActiveThreadId { get; }
    string? ActiveTurnId { get; }
    bool ActiveThreadLoaded { get; }
    int ActiveTurnCount { get; }

    IReadOnlyList<ProjectThreadState> GetThreads(AppSettings settings, ThreadScopeKey scope);
    IReadOnlyList<ProjectThreadState> GetProjectThreads(AppSettings settings, string projectPath);
    ProjectThreadState? GetActiveThread(AppSettings settings, ThreadScopeKey scope);
    void SetActiveThread(AppSettings settings, ThreadScopeKey scope, string threadId);
    void SetThreadArchived(AppSettings settings, string threadId, bool archived);
    bool HasThread(string threadId);
    ConversationWorkspaceSnapshot GetSnapshot(string? threadId);
    ConversationWorkspaceSnapshot RestoreThread(ProjectThreadState state);
    ConversationWorkspaceSnapshot ResetActiveConversation();
    void ReconcileHistory(string threadId, IEnumerable<CodexConversationTurnSnapshot> turns);
    CodexConversationTurnSnapshot? GetConversationTurn(string threadId, string turnId);
    void BeginTurn(
        string threadId,
        string prompt,
        IEnumerable<AttachmentReference>? attachments = null,
        ConversationOperationKind operation = ConversationOperationKind.DirectTurn);
    CodexConversationTurnSnapshot BindPendingTurn(string threadId, string turnId);
    void FailPendingTurn(string threadId, string detail);
    void AddGuidance(string threadId, string guidance);
    int GetActiveRollbackTurnCount(string threadId, string turnId);
    void SupersedeTurnsFrom(string threadId, string turnId);
    void RegisterTurn(string threadId, string turnId);
    void Select(string? threadId);
    void SetActiveThreadLoaded(bool loaded);
    void SetActiveTurn(string? turnId);
    bool IsLoaded(string threadId);
    bool IsRunning(string threadId);
    bool TryGetActiveTurn(string threadId, out string turnId);
    IReadOnlyList<KeyValuePair<string, string>> SnapshotActiveTurns();
    IReadOnlyList<string> SnapshotRunningThreadIds();
    void MarkLoaded(string threadId);
    void ClearRuntimeState();
    void RegisterCreated(ProjectThreadState state);
    void RegisterResumed(string threadId, IReadOnlyList<CodexConversationTurnSnapshot> turns);
    void RegisterTurnStarted(string threadId, string turnId, CodexTurnStatus status);
    void RegisterTurnFinished(string threadId);
    ConversationNotificationResult ApplyThreadNotification(CodexAppServerNotification notification);
    ConversationNotificationResult ApplyHarnessEvent(HarnessEvent harnessEvent);
    void RemoveRuntime(string threadId);
}

public enum ConversationWorkspaceChangeKind
{
    PendingTurnStarted,
    PendingTurnFailed,
    TurnStarted
}

public enum ConversationOperationKind
{
    External,
    DirectTurn,
    PromptEdit,
    QueuedFollowUp,
    CodeReview
}

public sealed record ConversationWorkspaceChangedEvent(
    string ThreadId,
    ConversationWorkspaceChangeKind Kind,
    ConversationWorkspaceSnapshot Snapshot,
    string? TurnId = null,
    CodexTurnStatus? TurnStatus = null,
    ConversationOperationKind Operation = ConversationOperationKind.External);
