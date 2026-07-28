using System.Text.Json.Nodes;

namespace SynthiaCode.Core.Codex.AppServer;

public sealed record CodexAppServerClientMetadata(string Name, string Title, string Version);

public sealed record CodexAppServerSession(string? UserAgent, string? PlatformFamily, string? PlatformOs);

public sealed record CodexInitializeOptions(
    bool ExperimentalApi = false,
    IReadOnlyList<string>? OptOutNotificationMethods = null)
{
    public static CodexInitializeOptions Default { get; } = new();
}

public sealed record CodexThreadStartOptions(
    string? Model = null,
    CodexSandbox? Sandbox = null,
    CodexApprovalPolicy? ApprovalPolicy = null,
    CodexApprovalsReviewer? ApprovalsReviewer = null,
    string? PermissionProfileId = null,
    string? Cwd = null,
    string? DeveloperInstructions = null,
    string? BaseInstructions = null)
{
    public static CodexThreadStartOptions Default { get; } = new();
}

public sealed record CodexThreadStartResult(
    string ThreadId,
    CodexActivePermissionProfile? ActivePermissionProfile = null);

public sealed record CodexThreadResumeRequest(
    string ThreadId,
    string Cwd,
    CodexSandbox? Sandbox,
    string? Model = null,
    CodexApprovalPolicy? ApprovalPolicy = null,
    CodexApprovalsReviewer? ApprovalsReviewer = null,
    string? PermissionProfileId = null,
    string? DeveloperInstructions = null,
    string? BaseInstructions = null);

public sealed record CodexThreadResumeResult(
    string ThreadId,
    IReadOnlyList<CodexConversationTurnSnapshot>? Turns = null,
    CodexActivePermissionProfile? ActivePermissionProfile = null);

public sealed record CodexThreadRollbackRequest(string ThreadId, int NumTurns);

public sealed record CodexThreadRollbackResult(
    string ThreadId,
    IReadOnlyList<CodexConversationTurnSnapshot> Turns);

public sealed record CodexThreadReadRequest(string ThreadId, bool IncludeTurns = true);

public sealed record CodexThreadReadResult(
    string ThreadId,
    IReadOnlyList<CodexConversationTurnSnapshot> Turns);

public sealed record CodexThreadListRequest(
    string? Cwd = null,
    bool? Archived = null,
    int? Limit = null,
    string? Cursor = null);

public sealed record CodexThreadSummary(
    string ThreadId,
    string Title,
    string Preview,
    string? Cwd,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? Status);

public sealed record CodexThreadListResult(
    IReadOnlyList<CodexThreadSummary> Threads,
    string? NextCursor);

public sealed record CodexThreadForkRequest(
    string ThreadId,
    string Cwd,
    CodexSandbox? Sandbox,
    string? Model = null,
    CodexApprovalPolicy? ApprovalPolicy = null,
    CodexApprovalsReviewer? ApprovalsReviewer = null,
    string? PermissionProfileId = null,
    string? DeveloperInstructions = null,
    string? BaseInstructions = null);

public sealed record CodexThreadForkResult(
    string ThreadId,
    CodexActivePermissionProfile? ActivePermissionProfile = null);

public abstract record CodexUserInput;

public sealed record CodexTextInput(string Text) : CodexUserInput;

public sealed record CodexImageInput(string DataUrl) : CodexUserInput;

public sealed record CodexLocalImageInput(string Path) : CodexUserInput;

public sealed record CodexMentionInput(string Name, string Path) : CodexUserInput;

public sealed record CodexSkillInput(string Name, string Path) : CodexUserInput;

public sealed record CodexTurnStartRequest(
    string ThreadId,
    IReadOnlyList<CodexUserInput> Inputs,
    string Cwd,
    CodexSandbox? Sandbox,
    string? Model = null,
    CodexReasoningEffort? ReasoningEffort = null,
    CodexServiceTierSelection ServiceTier = CodexServiceTierSelection.Inherit,
    CodexApprovalPolicy? ApprovalPolicy = null,
    CodexApprovalsReviewer? ApprovalsReviewer = null,
    string? PermissionProfileId = null)
{
    public CodexTurnStartRequest(
        string ThreadId,
        string Prompt,
        string Cwd,
        CodexSandbox? Sandbox,
        string? Model = null,
        CodexReasoningEffort? ReasoningEffort = null,
        CodexServiceTierSelection ServiceTier = CodexServiceTierSelection.Inherit,
        CodexApprovalPolicy? ApprovalPolicy = null,
        CodexApprovalsReviewer? ApprovalsReviewer = null,
        string? PermissionProfileId = null)
        : this(
            ThreadId,
            [new CodexTextInput(Prompt)],
            Cwd,
            Sandbox,
            Model,
            ReasoningEffort,
            ServiceTier,
            ApprovalPolicy,
            ApprovalsReviewer,
            PermissionProfileId)
    {
    }

    public string Prompt => string.Join(
        Environment.NewLine,
        Inputs.OfType<CodexTextInput>().Select(input => input.Text));
}

public sealed record CodexTurnStartResult(string TurnId);

public sealed record CodexTurnSteerRequest(
    string ThreadId,
    string ExpectedTurnId,
    IReadOnlyList<CodexUserInput> Inputs)
{
    public CodexTurnSteerRequest(string threadId, string expectedTurnId, string prompt)
        : this(threadId, expectedTurnId, [new CodexTextInput(prompt)])
    {
    }

    public string Prompt => string.Join(
        Environment.NewLine,
        Inputs.OfType<CodexTextInput>().Select(input => input.Text));
}

public sealed record CodexTurnSteerResult(string TurnId);

public sealed record CodexModelOption(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault,
    bool Hidden,
    CodexReasoningEffort? DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningOption> SupportedReasoningEfforts,
    IReadOnlyList<CodexServiceTierOption> ServiceTiers,
    string? AvailabilityMessage,
    IReadOnlyList<string>? AdditionalSpeedTiers = null,
    IReadOnlyList<CodexInputModality>? InputModalities = null)
{
    public CodexServiceTierOption? FastServiceTier => ServiceTiers.FirstOrDefault(tier =>
        string.Equals(tier.Id, "fast", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tier.Name, "Fast", StringComparison.OrdinalIgnoreCase));

    public bool SupportsFastMode =>
        AdditionalSpeedTiers?.Any(tier =>
            string.Equals(tier, "fast", StringComparison.OrdinalIgnoreCase)) == true ||
        FastServiceTier is not null;

    public bool SupportsImageInput =>
        (InputModalities ?? [CodexInputModality.Text, CodexInputModality.Image])
        .Contains(CodexInputModality.Image);
}

public enum CodexInputModality
{
    Text,
    Image
}

public sealed record CodexReasoningOption(CodexReasoningEffort Effort, string Description)
{
    public string ProtocolValue => Effort.ToProtocolValue();

    public string DisplayName => Effort.ToDisplayName();
}

public sealed record CodexServiceTierOption(string Id, string Name, string Description);

public sealed record AppServerNotification(string Method, JsonObject Params);

// This is the single seam between the app-server wire protocol and application
// consumers. A generated protocol client can replace Decode without changing the
// coordinator, view models, or thread projections.
public enum CodexAppServerNotificationKind
{
    Unknown,
    ThreadStarted,
    ThreadArchived,
    ThreadUnarchived,
    ThreadTokenUsageUpdated,
    ThreadCompacted,
    TurnStarted,
    TurnCompleted,
    TurnPlanUpdated,
    ItemStarted,
    ItemCompleted,
    AgentMessageDelta,
    McpToolCallProgress,
    AccountRateLimitsUpdated,
    AccountUpdated,
    AccountLoginCompleted,
    AccountNotification,
    SkillsChanged,
    ServerRequestResolved
}

public static class CodexAppServerNotificationMethods
{
    public const string ThreadStarted = "thread/started";
    public const string ThreadArchived = "thread/archived";
    public const string ThreadUnarchived = "thread/unarchived";
    public const string ThreadTokenUsageUpdated = "thread/tokenUsage/updated";
    public const string ThreadCompacted = "thread/compacted";
    public const string TurnStarted = "turn/started";
    public const string TurnCompleted = "turn/completed";
    public const string TurnPlanUpdated = "turn/plan/updated";
    public const string ItemStarted = "item/started";
    public const string ItemCompleted = "item/completed";
    public const string AgentMessageDelta = "item/agentMessage/delta";
    public const string McpToolCallProgress = "item/mcpToolCall/progress";
    public const string AccountRateLimitsUpdated = "account/rateLimits/updated";
    public const string AccountUpdated = "account/updated";
    public const string AccountLoginCompleted = "account/login/completed";
    public const string SkillsChanged = "skills/changed";
    public const string ServerRequestResolved = "serverRequest/resolved";
}

public sealed record CodexAppServerNotification(
    CodexAppServerNotificationKind Kind,
    string Method,
    JsonObject Params,
    string? ThreadId,
    string? TurnId,
    string? ItemId,
    string? TurnStatus,
    CodexRequestId? RequestId,
    bool? IsArchived,
    JsonObject? RateLimits)
{
    public static CodexAppServerNotification Decode(AppServerNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var parameters = notification.Params;
        var kind = GetKind(notification.Method);
        var threadId = ReadString(parameters, "threadId")
            ?? ReadString(parameters, "thread.id")
            ?? ReadString(parameters, "turn.threadId");
        var turnId = ReadString(parameters, "turnId") ?? ReadString(parameters, "turn.id");
        var itemId = ReadString(parameters, "itemId") ?? ReadString(parameters, "item.id");
        var turnStatus = ReadString(parameters, "status") ?? ReadString(parameters, "turn.status");
        var requestId = kind == CodexAppServerNotificationKind.ServerRequestResolved
            ? ReadRequestId(parameters)
            : null;
        bool? isArchived = kind switch
        {
            CodexAppServerNotificationKind.ThreadArchived => true,
            CodexAppServerNotificationKind.ThreadUnarchived => false,
            _ => null
        };
        var rateLimits = kind == CodexAppServerNotificationKind.AccountRateLimitsUpdated
            ? parameters["rateLimits"] as JsonObject
            : null;

        return new CodexAppServerNotification(
            kind,
            notification.Method,
            parameters,
            threadId,
            turnId,
            itemId,
            turnStatus,
            requestId,
            isArchived,
            rateLimits);
    }

    public string? ReadString(string path) => ReadString(Params, path);

    public JsonObject? ReadObject(string path) => ReadNode(Params, path) as JsonObject;

    private static CodexAppServerNotificationKind GetKind(string method) => method switch
    {
        CodexAppServerNotificationMethods.ThreadStarted => CodexAppServerNotificationKind.ThreadStarted,
        CodexAppServerNotificationMethods.ThreadArchived => CodexAppServerNotificationKind.ThreadArchived,
        CodexAppServerNotificationMethods.ThreadUnarchived => CodexAppServerNotificationKind.ThreadUnarchived,
        CodexAppServerNotificationMethods.ThreadTokenUsageUpdated => CodexAppServerNotificationKind.ThreadTokenUsageUpdated,
        CodexAppServerNotificationMethods.ThreadCompacted => CodexAppServerNotificationKind.ThreadCompacted,
        CodexAppServerNotificationMethods.TurnStarted => CodexAppServerNotificationKind.TurnStarted,
        CodexAppServerNotificationMethods.TurnCompleted => CodexAppServerNotificationKind.TurnCompleted,
        CodexAppServerNotificationMethods.TurnPlanUpdated => CodexAppServerNotificationKind.TurnPlanUpdated,
        CodexAppServerNotificationMethods.ItemStarted => CodexAppServerNotificationKind.ItemStarted,
        CodexAppServerNotificationMethods.ItemCompleted => CodexAppServerNotificationKind.ItemCompleted,
        CodexAppServerNotificationMethods.AgentMessageDelta => CodexAppServerNotificationKind.AgentMessageDelta,
        CodexAppServerNotificationMethods.McpToolCallProgress => CodexAppServerNotificationKind.McpToolCallProgress,
        CodexAppServerNotificationMethods.AccountRateLimitsUpdated => CodexAppServerNotificationKind.AccountRateLimitsUpdated,
        CodexAppServerNotificationMethods.AccountUpdated => CodexAppServerNotificationKind.AccountUpdated,
        CodexAppServerNotificationMethods.AccountLoginCompleted => CodexAppServerNotificationKind.AccountLoginCompleted,
        CodexAppServerNotificationMethods.SkillsChanged => CodexAppServerNotificationKind.SkillsChanged,
        CodexAppServerNotificationMethods.ServerRequestResolved => CodexAppServerNotificationKind.ServerRequestResolved,
        _ when method.StartsWith("account/", StringComparison.Ordinal) => CodexAppServerNotificationKind.AccountNotification,
        _ => CodexAppServerNotificationKind.Unknown
    };

    private static CodexRequestId? ReadRequestId(JsonObject parameters)
    {
        var node = parameters["requestId"] ?? parameters["id"];
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var integerId))
        {
            return CodexRequestId.FromInteger(integerId);
        }

        return value.TryGetValue<string>(out var stringId) && !string.IsNullOrWhiteSpace(stringId)
            ? CodexRequestId.FromString(stringId)
            : null;
    }

    private static string? ReadString(JsonObject parameters, string path) =>
        ReadNode(parameters, path) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? ReadNode(JsonObject parameters, string path)
    {
        JsonNode? current = parameters;
        foreach (var segment in path.Split('.'))
        {
            current = current is JsonObject currentObject ? currentObject[segment] : null;
        }

        return current;
    }
}

public sealed class AppServerConnectionFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public enum CodexSandbox
{
    ReadOnly,
    WorkspaceWrite,
    DangerFullAccess
}

public enum CodexReasoningEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh
}

public enum CodexServiceTierSelection
{
    Inherit,
    Standard,
    Fast
}

public static class CodexSandboxExtensions
{
    public static string ToProtocolValue(this CodexSandbox sandbox)
    {
        return sandbox switch
        {
            CodexSandbox.ReadOnly => "read-only",
            CodexSandbox.WorkspaceWrite => "workspace-write",
            CodexSandbox.DangerFullAccess => "danger-full-access",
            _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "Unknown sandbox value.")
        };
    }

    public static JsonObject ToTurnSandboxPolicy(this CodexSandbox sandbox)
    {
        return new JsonObject
        {
            ["type"] = sandbox switch
            {
                CodexSandbox.ReadOnly => "readOnly",
                CodexSandbox.WorkspaceWrite => "workspaceWrite",
                CodexSandbox.DangerFullAccess => "dangerFullAccess",
                _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "Unknown sandbox value.")
            }
        };
    }
}

public static class CodexReasoningEffortExtensions
{
    public static string ToProtocolValue(this CodexReasoningEffort effort)
    {
        return effort switch
        {
            CodexReasoningEffort.None => "none",
            CodexReasoningEffort.Minimal => "minimal",
            CodexReasoningEffort.Low => "low",
            CodexReasoningEffort.Medium => "medium",
            CodexReasoningEffort.High => "high",
            CodexReasoningEffort.XHigh => "xhigh",
            _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort value.")
        };
    }

    public static string ToDisplayName(this CodexReasoningEffort effort)
    {
        return effort switch
        {
            CodexReasoningEffort.None => "None",
            CodexReasoningEffort.Minimal => "Minimal",
            CodexReasoningEffort.Low => "Low",
            CodexReasoningEffort.Medium => "Medium",
            CodexReasoningEffort.High => "High",
            CodexReasoningEffort.XHigh => "Extra high",
            _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort value.")
        };
    }
}
