using SynthiaCode.Core.Codex.AppServer;
using Xunit;

public sealed class QueuedTurnOptionResolverTests
{
    [Fact]
    public void Resolve_uses_the_current_catalog_and_managed_permission_policy()
    {
        var options = new QueuedTurnOptionsSnapshot
        {
            Model = "gpt-queued",
            ReasoningEffort = CodexReasoningEffort.High,
            ServiceTier = CodexServiceTierSelection.Fast,
            PermissionMode = CodexPermissionMode.AskForApproval,
            PermissionProfileId = ":workspace",
            ApprovalPolicy = CodexApprovalPolicy.OnRequest,
            ApprovalsReviewer = CodexApprovalsReviewer.User
        };
        var model = Model(
            "gpt-queued",
            isDefault: true,
            supportsFast: true,
            reasoningEfforts: [CodexReasoningEffort.High]);
        var requirements = new CodexExecutionPolicyRequirements(
            [],
            [CodexApprovalPolicy.OnRequest],
            [CodexApprovalsReviewer.User],
            [":workspace"]);

        var resolved = QueuedTurnOptionResolver.Resolve(
            options,
            [model],
            EmptyConfig(),
            requirements,
            new CodexPermissionProfileListResult([], null, IsSupported: true));

        Assert.Same(model, resolved.Model);
        Assert.Equal(":workspace", resolved.Permissions.PermissionProfileId);
        Assert.Equal(CodexApprovalPolicy.OnRequest, resolved.Permissions.ApprovalPolicy);
        Assert.Equal(CodexApprovalsReviewer.User, resolved.Permissions.ApprovalsReviewer);
    }

    [Fact]
    public void Resolve_rejects_a_model_removed_from_the_refreshed_catalog()
    {
        var options = new QueuedTurnOptionsSnapshot
        {
            Model = "gpt-removed",
            PermissionMode = CodexPermissionMode.Custom
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            QueuedTurnOptionResolver.Resolve(
                options,
                [Model("gpt-current", isDefault: true)],
                EmptyConfig(),
                CodexExecutionPolicyRequirements.Unrestricted,
                new CodexPermissionProfileListResult([], null, IsSupported: true)));

        Assert.Contains("gpt-removed", error.Message, StringComparison.Ordinal);
        Assert.Contains("no longer available", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_rejects_reasoning_or_fast_options_removed_from_the_refreshed_model()
    {
        var options = new QueuedTurnOptionsSnapshot
        {
            Model = "gpt-current",
            ReasoningEffort = CodexReasoningEffort.XHigh,
            ServiceTier = CodexServiceTierSelection.Fast,
            PermissionMode = CodexPermissionMode.Custom
        };

        var reasoningError = Assert.Throws<InvalidOperationException>(() =>
            QueuedTurnOptionResolver.Resolve(
                options,
                [Model("gpt-current", isDefault: true, reasoningEfforts: [CodexReasoningEffort.Medium])],
                EmptyConfig(),
                CodexExecutionPolicyRequirements.Unrestricted,
                new CodexPermissionProfileListResult([], null, IsSupported: true)));
        Assert.Contains("reasoning", reasoningError.Message, StringComparison.OrdinalIgnoreCase);

        options.ReasoningEffort = CodexReasoningEffort.Medium;
        var fastError = Assert.Throws<InvalidOperationException>(() =>
            QueuedTurnOptionResolver.Resolve(
                options,
                [Model("gpt-current", isDefault: true, reasoningEfforts: [CodexReasoningEffort.Medium])],
                EmptyConfig(),
                CodexExecutionPolicyRequirements.Unrestricted,
                new CodexPermissionProfileListResult([], null, IsSupported: true)));
        Assert.Contains("Fast", fastError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_rejects_a_captured_permission_choice_blocked_by_new_managed_policy()
    {
        var options = new QueuedTurnOptionsSnapshot
        {
            PermissionMode = CodexPermissionMode.AskForApproval,
            PermissionProfileId = ":workspace",
            ApprovalPolicy = CodexApprovalPolicy.OnRequest,
            ApprovalsReviewer = CodexApprovalsReviewer.User
        };
        var requirements = new CodexExecutionPolicyRequirements(
            [],
            [CodexApprovalPolicy.OnRequest],
            [CodexApprovalsReviewer.AutoReview],
            [":workspace"]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            QueuedTurnOptionResolver.Resolve(
                options,
                [Model("gpt-current", isDefault: true)],
                EmptyConfig(),
                requirements,
                new CodexPermissionProfileListResult([], null, IsSupported: true)));

        Assert.Contains("blocked by managed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_rejects_a_named_profile_removed_before_dispatch()
    {
        var options = new QueuedTurnOptionsSnapshot
        {
            PermissionMode = CodexPermissionMode.Custom,
            PermissionProfileId = "team-safe"
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            QueuedTurnOptionResolver.Resolve(
                options,
                [Model("gpt-current", isDefault: true)],
                EmptyConfig(),
                CodexExecutionPolicyRequirements.Unrestricted,
                new CodexPermissionProfileListResult(
                    [new CodexPermissionProfileSummary("other-profile", null, Allowed: true)],
                    null,
                    IsSupported: true)));

        Assert.Contains("no longer available", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CodexModelOption Model(
        string model,
        bool isDefault,
        bool supportsFast = false,
        IReadOnlyList<CodexReasoningEffort>? reasoningEfforts = null) => new(
            model,
            model,
            model,
            string.Empty,
            isDefault,
            Hidden: false,
            reasoningEfforts?.FirstOrDefault(),
            reasoningEfforts?.Select(effort => new CodexReasoningOption(effort, effort.ToString())).ToArray() ?? [],
            supportsFast ? [new CodexServiceTierOption("fast", "Fast", "Fast")] : [],
            AvailabilityMessage: null);

    private static CodexExecutionPolicyConfig EmptyConfig() =>
        new(null, null, null, null, new Dictionary<string, string?>());
}
