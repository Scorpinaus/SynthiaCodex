using System.Text.Json.Nodes;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed record CodexAppServerClientMetadata(string Name, string Title, string Version);

public sealed record CodexAppServerSession(string? UserAgent, string? PlatformFamily, string? PlatformOs);

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

public sealed record CodexTurnStartRequest(
    string ThreadId,
    string Prompt,
    string Cwd,
    CodexSandbox Sandbox,
    string? Model = null,
    CodexReasoningEffort? ReasoningEffort = null);

public sealed record CodexTurnStartResult(string TurnId);

public sealed record CodexModelOption(
    string Id,
    string Model,
    string DisplayName,
    bool IsDefault,
    IReadOnlyList<string> SupportedReasoningEfforts);

public sealed record AppServerNotification(string Method, JsonObject Params);

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
