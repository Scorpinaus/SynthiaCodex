namespace SynthiaCode.Core.Harnesses;

public abstract record HarnessEvent(
    HarnessId HarnessId,
    string? RemoteConversationId,
    string? RemoteTurnId,
    DateTimeOffset Timestamp);

public sealed record ConversationStartedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, null, Timestamp);

public sealed record ConversationArchivedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    bool IsArchived,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, null, Timestamp);

public sealed record TurnStartedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    string RemoteTurnId,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public sealed record AssistantTextDeltaEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    string RemoteTurnId,
    string MessageId,
    string Delta,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public sealed record TurnDiffChangedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    string RemoteTurnId,
    string Diff,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public sealed record ActivityChangedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    string? RemoteTurnId,
    ActivityItem Activity,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public sealed record ContextUsageChangedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    long UsedTokens,
    long WindowTokens,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, null, Timestamp);

public sealed record ContextCompactedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    string? RemoteTurnId,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public sealed record TurnCompletedEvent(
    HarnessId HarnessId,
    string RemoteConversationId,
    string RemoteTurnId,
    ConversationTurnStatus Status,
    string? Error,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public sealed record AuthenticationRequiredEvent(
    HarnessId HarnessId,
    string? RemoteConversationId,
    string Detail,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, null, Timestamp);

public sealed record HarnessDiagnosticEvent(
    HarnessId HarnessId,
    string? RemoteConversationId,
    string? RemoteTurnId,
    string EventName,
    string Summary,
    bool IsError,
    DateTimeOffset Timestamp)
    : HarnessEvent(HarnessId, RemoteConversationId, RemoteTurnId, Timestamp);

public enum ApprovalRequestKind
{
    CommandExecution,
    FileChange,
    AdditionalPermissions,
    Other
}

public sealed record ApprovalOption(
    string Id,
    string DisplayName,
    string Description,
    bool IsDestructive = false);

public sealed record ApprovalRequest(
    string Id,
    HarnessId HarnessId,
    string? RemoteConversationId,
    string? RemoteTurnId,
    ApprovalRequestKind Kind,
    string Title,
    string Detail,
    string? WorkingDirectory,
    IReadOnlyList<ApprovalOption> Options);

public sealed record ApprovalResponse(string RequestId, string OptionId);
