using System.Text.Json.Nodes;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public enum CodexRequestIdKind
{
    Integer,
    String
}

public readonly record struct CodexRequestId
{
    private CodexRequestId(CodexRequestIdKind kind, long integerValue, string? stringValue)
    {
        Kind = kind;
        IntegerValue = integerValue;
        StringValue = stringValue;
    }

    public CodexRequestIdKind Kind { get; }

    public long IntegerValue { get; }

    public string? StringValue { get; }

    public static CodexRequestId FromInteger(long value) => new(CodexRequestIdKind.Integer, value, null);

    public static CodexRequestId FromString(string value) => new(
        CodexRequestIdKind.String,
        0,
        string.IsNullOrEmpty(value) ? throw new ArgumentException("Request ID cannot be empty.", nameof(value)) : value);

    public JsonNode ToJsonNode() => Kind switch
    {
        CodexRequestIdKind.Integer => JsonValue.Create(IntegerValue),
        CodexRequestIdKind.String => JsonValue.Create(StringValue!),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown request ID kind.")
    };

    public override string ToString() => Kind == CodexRequestIdKind.Integer
        ? IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : StringValue ?? string.Empty;
}

public sealed record CodexServerRequest(
    CodexRequestId RequestId,
    string Method,
    JsonObject Params,
    CodexServerRequestPayload Payload);

public abstract record CodexServerRequestPayload;

public sealed record CodexNetworkApprovalContext(string Host, string Protocol, int? Port = null);

public sealed record CodexCommandApprovalRequest(
    string ThreadId,
    string TurnId,
    string ItemId,
    long StartedAtMs,
    string? Command,
    string? Cwd,
    string? Reason,
    CodexNetworkApprovalContext? NetworkContext,
    IReadOnlyList<string> ProposedExecPolicyAmendment,
    IReadOnlyList<string> AvailableDecisions,
    string? ApprovalId) : CodexServerRequestPayload;

public sealed record CodexFileChangeApprovalRequest(
    string ThreadId,
    string TurnId,
    string ItemId,
    long StartedAtMs,
    string? Reason,
    string? GrantRoot) : CodexServerRequestPayload;

public sealed record CodexPermissionApprovalRequest(
    string ThreadId,
    string TurnId,
    string ItemId,
    long StartedAtMs,
    string Cwd,
    string? Reason,
    JsonObject RequestedPermissions) : CodexServerRequestPayload;

public sealed record CodexUnsupportedServerRequest(string Method) : CodexServerRequestPayload;

public enum CodexApprovalDecision
{
    Accept,
    AcceptForSession,
    Decline,
    Cancel
}

public enum CodexPermissionGrantScope
{
    Turn,
    Session
}

public sealed record CodexServerRequestResponse(JsonObject Result)
{
    public static CodexServerRequestResponse Command(CodexApprovalDecision decision) =>
        Approval(decision);

    public static CodexServerRequestResponse FileChange(CodexApprovalDecision decision) =>
        Approval(decision);

    public static CodexServerRequestResponse Permissions(
        JsonObject grantedPermissions,
        CodexPermissionGrantScope scope = CodexPermissionGrantScope.Turn)
    {
        ArgumentNullException.ThrowIfNull(grantedPermissions);
        return new CodexServerRequestResponse(new JsonObject
        {
            ["permissions"] = grantedPermissions.DeepClone(),
            ["scope"] = scope == CodexPermissionGrantScope.Session ? "session" : "turn"
        });
    }

    private static CodexServerRequestResponse Approval(CodexApprovalDecision decision) => new(new JsonObject
    {
        ["decision"] = decision switch
        {
            CodexApprovalDecision.Accept => "accept",
            CodexApprovalDecision.AcceptForSession => "acceptForSession",
            CodexApprovalDecision.Decline => "decline",
            CodexApprovalDecision.Cancel => "cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown approval decision.")
        }
    });
}

public enum CodexApprovalPolicy
{
    Untrusted,
    OnRequest,
    Never,
    OnFailureDeprecated,
    Granular
}

public enum CodexApprovalsReviewer
{
    User,
    AutoReview,
    GuardianSubagentLegacy
}

public sealed record CodexExecutionPolicyConfig(
    CodexSandbox? Sandbox,
    CodexApprovalPolicy? ApprovalPolicy,
    CodexApprovalsReviewer? ApprovalsReviewer,
    bool? WorkspaceWriteNetworkAccess,
    IReadOnlyDictionary<string, string?> Origins);

public sealed record CodexExecutionPolicyRequirements(
    IReadOnlyList<CodexSandbox> AllowedSandboxes,
    IReadOnlyList<CodexApprovalPolicy> AllowedApprovalPolicies)
{
    public static CodexExecutionPolicyRequirements Unrestricted { get; } = new([], []);
}

public static class CodexExecutionPolicyExtensions
{
    public static string ToProtocolValue(this CodexApprovalPolicy policy) => policy switch
    {
        CodexApprovalPolicy.Untrusted => "untrusted",
        CodexApprovalPolicy.OnRequest => "on-request",
        CodexApprovalPolicy.Never => "never",
        CodexApprovalPolicy.OnFailureDeprecated => "on-failure",
        CodexApprovalPolicy.Granular => throw new InvalidOperationException("A granular approval policy requires its structured configuration."),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown approval policy.")
    };

    public static string ToProtocolValue(this CodexApprovalsReviewer reviewer) => reviewer switch
    {
        CodexApprovalsReviewer.User => "user",
        CodexApprovalsReviewer.AutoReview => "auto_review",
        CodexApprovalsReviewer.GuardianSubagentLegacy => "guardian_subagent",
        _ => throw new ArgumentOutOfRangeException(nameof(reviewer), reviewer, "Unknown approval reviewer.")
    };
}
