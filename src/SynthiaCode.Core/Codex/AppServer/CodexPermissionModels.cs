namespace SynthiaCode.Core.Codex.AppServer;

public enum CodexPermissionMode
{
    Unknown,
    AskForApproval,
    ApproveForMe,
    Custom,
    CustomLegacy
}

public sealed record CodexPermissionProfileSummary(string Id, string? Description, bool Allowed);

public sealed record CodexPermissionProfileListRequest(
    string Cwd,
    string? Cursor = null,
    int? Limit = null);

public sealed record CodexPermissionProfileListResult(
    IReadOnlyList<CodexPermissionProfileSummary> Profiles,
    string? NextCursor,
    bool IsSupported = true);

public sealed record CodexPermissionCapabilities(
    bool SupportsPermissionProfiles,
    bool SupportsAutoReview);

public sealed record CodexActivePermissionProfile(string Id, string? Description = null);

public sealed record CodexResolvedPermissionMode(
    bool IsAvailable,
    string? UnavailableReason,
    string? PermissionProfileId,
    CodexSandbox? Sandbox,
    CodexApprovalPolicy? ApprovalPolicy,
    CodexApprovalsReviewer? ApprovalsReviewer,
    bool UsesLegacyFallback = false)
{
    public static CodexResolvedPermissionMode Unavailable(string reason) =>
        new(false, reason, null, null, null, null);
}

public static class CodexPermissionModeResolver
{
    private const string WorkspaceProfileId = ":workspace";

    public static CodexResolvedPermissionMode Resolve(
        CodexPermissionMode mode,
        string? customProfileId,
        IReadOnlyList<CodexPermissionProfileSummary> profiles,
        CodexExecutionPolicyRequirements requirements,
        CodexPermissionCapabilities capabilities,
        CodexSandbox? legacySandbox = null,
        CodexApprovalPolicy? legacyApprovalPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(capabilities);

        return mode switch
        {
            CodexPermissionMode.AskForApproval => ResolveWorkspaceMode(
                CodexApprovalsReviewer.User,
                capabilities,
                requirements),
            CodexPermissionMode.ApproveForMe => capabilities.SupportsAutoReview
                ? ResolveWorkspaceMode(CodexApprovalsReviewer.AutoReview, capabilities, requirements)
                : CodexResolvedPermissionMode.Unavailable("Automatic approval review is not supported by this Codex installation."),
            CodexPermissionMode.Custom => ResolveCustom(customProfileId, profiles, requirements, capabilities),
            CodexPermissionMode.CustomLegacy => ResolveLegacyCustom(legacySandbox, legacyApprovalPolicy, requirements),
            _ => CodexResolvedPermissionMode.Unavailable("The saved permission mode is not recognized.")
        };
    }

    private static CodexResolvedPermissionMode ResolveWorkspaceMode(
        CodexApprovalsReviewer reviewer,
        CodexPermissionCapabilities capabilities,
        CodexExecutionPolicyRequirements requirements)
    {
        if (!Allows(requirements.AllowedApprovalPolicies, CodexApprovalPolicy.OnRequest))
        {
            return CodexResolvedPermissionMode.Unavailable("On-request approvals are blocked by managed Codex requirements.");
        }

        if (!Allows(requirements.AllowedApprovalsReviewers, reviewer))
        {
            var label = reviewer == CodexApprovalsReviewer.AutoReview ? "Automatic review" : "User review";
            return CodexResolvedPermissionMode.Unavailable($"{label} is blocked by managed Codex requirements.");
        }

        if (capabilities.SupportsPermissionProfiles)
        {
            if (!Allows(requirements.AllowedPermissionProfiles, WorkspaceProfileId, StringComparer.Ordinal))
            {
                return CodexResolvedPermissionMode.Unavailable("The workspace permission profile is blocked by managed Codex requirements.");
            }

            return new CodexResolvedPermissionMode(
                true,
                null,
                WorkspaceProfileId,
                null,
                CodexApprovalPolicy.OnRequest,
                reviewer);
        }

        if (!Allows(requirements.AllowedSandboxes, CodexSandbox.WorkspaceWrite))
        {
            return CodexResolvedPermissionMode.Unavailable("Workspace-write sandboxing is blocked by managed Codex requirements.");
        }

        return new CodexResolvedPermissionMode(
            true,
            null,
            null,
            CodexSandbox.WorkspaceWrite,
            CodexApprovalPolicy.OnRequest,
            reviewer,
            UsesLegacyFallback: true);
    }

    private static CodexResolvedPermissionMode ResolveCustom(
        string? customProfileId,
        IReadOnlyList<CodexPermissionProfileSummary> profiles,
        CodexExecutionPolicyRequirements requirements,
        CodexPermissionCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(customProfileId))
        {
            return new CodexResolvedPermissionMode(true, null, null, null, null, null);
        }

        if (!capabilities.SupportsPermissionProfiles)
        {
            return CodexResolvedPermissionMode.Unavailable("Named permission profiles are not supported by this Codex installation.");
        }

        var profile = profiles.FirstOrDefault(item => string.Equals(item.Id, customProfileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return CodexResolvedPermissionMode.Unavailable("The selected permission profile is no longer available for this project.");
        }

        if (!profile.Allowed || !Allows(requirements.AllowedPermissionProfiles, profile.Id, StringComparer.Ordinal))
        {
            return CodexResolvedPermissionMode.Unavailable("The selected permission profile is blocked by managed Codex requirements.");
        }

        return new CodexResolvedPermissionMode(true, null, profile.Id, null, null, null);
    }

    private static CodexResolvedPermissionMode ResolveLegacyCustom(
        CodexSandbox? sandbox,
        CodexApprovalPolicy? approvalPolicy,
        CodexExecutionPolicyRequirements requirements)
    {
        if (sandbox is not null && !Allows(requirements.AllowedSandboxes, sandbox.Value))
        {
            return CodexResolvedPermissionMode.Unavailable("The saved legacy sandbox is blocked by managed Codex requirements.");
        }

        if (approvalPolicy is not null && !Allows(requirements.AllowedApprovalPolicies, approvalPolicy.Value))
        {
            return CodexResolvedPermissionMode.Unavailable("The saved legacy approval policy is blocked by managed Codex requirements.");
        }

        return new CodexResolvedPermissionMode(
            true,
            null,
            null,
            sandbox,
            approvalPolicy,
            CodexApprovalsReviewer.User,
            UsesLegacyFallback: true);
    }

    private static bool Allows<T>(IReadOnlyList<T> allowed, T value) where T : struct =>
        allowed.Count == 0 || allowed.Contains(value);

    private static bool Allows(
        IReadOnlyList<string> allowed,
        string value,
        StringComparer comparer) =>
        allowed.Count == 0 || allowed.Contains(value, comparer);
}

public static class CodexPermissionModeExtensions
{
    public static string ToSettingsValue(this CodexPermissionMode mode) => mode switch
    {
        CodexPermissionMode.AskForApproval => "ask-for-approval",
        CodexPermissionMode.ApproveForMe => "approve-for-me",
        CodexPermissionMode.Custom => "custom",
        CodexPermissionMode.CustomLegacy => "custom-legacy",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown permission mode.")
    };

    public static CodexPermissionMode ParsePermissionMode(this string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "ask-for-approval" => CodexPermissionMode.AskForApproval,
        "approve-for-me" => CodexPermissionMode.ApproveForMe,
        "custom" => CodexPermissionMode.Custom,
        "custom-legacy" => CodexPermissionMode.CustomLegacy,
        _ => CodexPermissionMode.Unknown
    };
}
