using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Codex.Configuration;

namespace SynthiaCode.Harnesses.Codex;

/// <summary>
/// Codex-only side features are intentionally separate from the portable harness
/// contract. Presentation surfaces opt into only the provider feature they use.
/// </summary>
public interface ICodexNotificationFeature
{
    event EventHandler<CodexAppServerNotification>? NotificationReceived;
}

public interface ICodexAccountFeature
{
    Task<CodexAccountReadResult> ReadAccountAsync(
        bool refreshToken = false,
        CancellationToken cancellationToken = default);

    Task<CodexAccountRateLimitsResult> ReadAccountRateLimitsAsync(
        CancellationToken cancellationToken = default);
}

public interface ICodexExecutionPolicyFeature
{
    Task<CodexExecutionPolicyConfig> ReadExecutionPolicyConfigAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default);

    Task<CodexExecutionPolicyRequirements> ReadExecutionPolicyRequirementsAsync(
        CancellationToken cancellationToken = default);

    Task<CodexPermissionProfileListResult> ListPermissionProfilesAsync(
        string cwd,
        CancellationToken cancellationToken = default);
}

public interface ICodexSkillsFeature : ICodexNotificationFeature
{
    Task<CodexSkillListResult> ListSkillsAsync(
        CodexSkillListRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexSkillConfigWriteResult> WriteSkillConfigAsync(
        CodexSkillConfigWriteRequest request,
        CancellationToken cancellationToken = default);
}

public interface ICodexConfigurationFeature
{
    Task<CodexEffectiveConfiguration> ReadEffectiveConfigurationAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default);
}

public interface ICodexApprovalFeature
{
    Task RespondToServerRequestAsync(
        CodexServerRequest request,
        CodexServerRequestResponse response,
        CancellationToken cancellationToken = default);
}
