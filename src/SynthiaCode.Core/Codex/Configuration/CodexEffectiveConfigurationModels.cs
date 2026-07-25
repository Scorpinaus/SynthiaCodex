namespace SynthiaCode.Core.Codex.Configuration;

public sealed record CodexEffectiveConfiguration(
    string? Model,
    string? ModelProvider,
    string? ReasoningEffort,
    string? ServiceTier,
    string? Profile,
    string? SandboxMode,
    string? ApprovalPolicy,
    string? ApprovalsReviewer,
    string? WebSearchMode,
    bool? SandboxNetworkAccess,
    IReadOnlyDictionary<string, string?> Origins,
    bool IsSupported = true)
{
    public static CodexEffectiveConfiguration Unsupported { get; } = new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        new Dictionary<string, string?>(StringComparer.Ordinal),
        IsSupported: false);
}
