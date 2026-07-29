namespace SynthiaCode.Core.Codex.AppServer;

public sealed record QueuedTurnOptionResolution(
    CodexModelOption Model,
    CodexResolvedPermissionMode Permissions);

public static class QueuedTurnOptionResolver
{
    public static QueuedTurnOptionResolution Resolve(
        QueuedTurnOptionsSnapshot options,
        IReadOnlyList<CodexModelOption> models,
        CodexExecutionPolicyConfig effectiveConfig,
        CodexExecutionPolicyRequirements requirements,
        CodexPermissionProfileListResult profileResult)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(effectiveConfig);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(profileResult);

        var visibleModels = models
            .Where(model => !model.Hidden)
            .DistinctBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var model = string.IsNullOrWhiteSpace(options.Model)
            ? visibleModels.FirstOrDefault(candidate => candidate.IsDefault) ?? visibleModels.FirstOrDefault()
            : visibleModels.FirstOrDefault(candidate =>
                string.Equals(candidate.Model, options.Model, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            var description = string.IsNullOrWhiteSpace(options.Model)
                ? "No available model was returned by the refreshed Codex catalog."
                : $"The queued model '{options.Model}' is no longer available.";
            throw new InvalidOperationException(description);
        }

        if (options.ReasoningEffort is { } reasoning &&
            !model.SupportedReasoningEfforts.Any(option => option.Effort == reasoning))
        {
            throw new InvalidOperationException(
                $"The queued reasoning effort '{reasoning.ToDisplayName()}' is no longer available for {model.DisplayName}.");
        }

        if (options.ServiceTier == CodexServiceTierSelection.Fast && !model.SupportsFastMode)
        {
            throw new InvalidOperationException(
                $"Fast service is no longer available for the queued model {model.DisplayName}.");
        }

        var mode = options.PermissionMode ?? InferPermissionMode(options);
        var capabilities = new CodexPermissionCapabilities(
            profileResult.IsSupported,
            SupportsAutoReview: true);
        var permissions = mode == CodexPermissionMode.Custom &&
                          string.IsNullOrWhiteSpace(options.PermissionProfileId)
            ? ResolveConfigDefault(effectiveConfig, requirements)
            : CodexPermissionModeResolver.Resolve(
                mode,
                options.PermissionProfileId,
                profileResult.Profiles,
                requirements,
                capabilities,
                options.Sandbox,
                options.ApprovalPolicy);
        if (!permissions.IsAvailable)
        {
            throw new InvalidOperationException(
                permissions.UnavailableReason ??
                "The queued permission mode is no longer available under the current managed policy.");
        }

        return new QueuedTurnOptionResolution(model, permissions);
    }

    private static CodexPermissionMode InferPermissionMode(QueuedTurnOptionsSnapshot options)
    {
        if (string.Equals(options.PermissionProfileId, ":workspace", StringComparison.Ordinal))
        {
            return options.ApprovalsReviewer == CodexApprovalsReviewer.AutoReview
                ? CodexPermissionMode.ApproveForMe
                : CodexPermissionMode.AskForApproval;
        }

        if (!string.IsNullOrWhiteSpace(options.PermissionProfileId))
        {
            return CodexPermissionMode.Custom;
        }

        if (options.ApprovalPolicy == CodexApprovalPolicy.OnRequest &&
            options.Sandbox == CodexSandbox.WorkspaceWrite &&
            options.ApprovalsReviewer is CodexApprovalsReviewer.User or CodexApprovalsReviewer.AutoReview)
        {
            return options.ApprovalsReviewer == CodexApprovalsReviewer.AutoReview
                ? CodexPermissionMode.ApproveForMe
                : CodexPermissionMode.AskForApproval;
        }

        return options.Sandbox is not null || options.ApprovalPolicy is not null
            ? CodexPermissionMode.CustomLegacy
            : CodexPermissionMode.Custom;
    }

    private static CodexResolvedPermissionMode ResolveConfigDefault(
        CodexExecutionPolicyConfig effectiveConfig,
        CodexExecutionPolicyRequirements requirements)
    {
        if (effectiveConfig.Sandbox is { } sandbox &&
            requirements.AllowedSandboxes.Count > 0 &&
            !requirements.AllowedSandboxes.Contains(sandbox))
        {
            return CodexResolvedPermissionMode.Unavailable(
                "The current config.toml sandbox is blocked by managed Codex requirements.");
        }

        if (effectiveConfig.ApprovalPolicy is { } approval &&
            requirements.AllowedApprovalPolicies.Count > 0 &&
            !requirements.AllowedApprovalPolicies.Contains(approval))
        {
            return CodexResolvedPermissionMode.Unavailable(
                "The current config.toml approval policy is blocked by managed Codex requirements.");
        }

        if (effectiveConfig.ApprovalsReviewer is { } reviewer &&
            requirements.AllowedApprovalsReviewers.Count > 0 &&
            !requirements.AllowedApprovalsReviewers.Contains(reviewer))
        {
            return CodexResolvedPermissionMode.Unavailable(
                "The current config.toml approvals reviewer is blocked by managed Codex requirements.");
        }

        return new CodexResolvedPermissionMode(true, null, null, null, null, null);
    }
}
