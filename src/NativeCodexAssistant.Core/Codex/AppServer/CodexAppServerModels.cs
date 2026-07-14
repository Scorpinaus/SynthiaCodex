using System.Text.Json.Nodes;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed record CodexAppServerClientMetadata(string Name, string Title, string Version);

public sealed record CodexAppServerSession(string? UserAgent, string? PlatformFamily, string? PlatformOs);

public sealed record CodexInitializeOptions(
    bool ExperimentalApi = false,
    IReadOnlyList<string>? OptOutNotificationMethods = null)
{
    public static CodexInitializeOptions Default { get; } = new(
        ExperimentalApi: false,
        OptOutNotificationMethods: ["thread/tokenUsage/updated"]);
}

public sealed record CodexThreadStartOptions(string? Model = null, CodexSandbox? Sandbox = null)
{
    public static CodexThreadStartOptions Default { get; } = new();
}

public sealed record CodexThreadStartResult(string ThreadId);

public sealed record CodexThreadResumeRequest(
    string ThreadId,
    string Cwd,
    CodexSandbox Sandbox,
    string? Model = null);

public sealed record CodexThreadResumeResult(string ThreadId);

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
    CodexSandbox Sandbox,
    string? Model = null);

public sealed record CodexThreadForkResult(string ThreadId);

public sealed record CodexTurnStartRequest(
    string ThreadId,
    string Prompt,
    string Cwd,
    CodexSandbox Sandbox,
    string? Model = null,
    CodexReasoningEffort? ReasoningEffort = null);

public sealed record CodexTurnStartResult(string TurnId);

public sealed record CodexTurnSteerRequest(string ThreadId, string ExpectedTurnId, string Prompt);

public sealed record CodexTurnSteerResult(string TurnId);

public sealed record CodexModelOption(
    string Id,
    string Model,
    string DisplayName,
    bool IsDefault,
    IReadOnlyList<string> SupportedReasoningEfforts);

public sealed record AppServerNotification(string Method, JsonObject Params);

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
}
