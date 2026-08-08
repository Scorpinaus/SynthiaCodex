using System.IO;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.App.Services;

public sealed record ProjectTrustAuthorizationResult(
    string NormalizedPath,
    CodexProjectTrustLevel TrustLevel,
    bool IsAuthorized,
    bool IsCanceled,
    string? FailureMessage)
{
    public static ProjectTrustAuthorizationResult Authorized(
        string path,
        CodexProjectTrustLevel trustLevel) =>
        new(path, trustLevel, IsAuthorized: true, IsCanceled: false, FailureMessage: null);

    public static ProjectTrustAuthorizationResult Canceled(string path) =>
        new(path, CodexProjectTrustLevel.Unknown, IsAuthorized: false, IsCanceled: true, FailureMessage: null);

    public static ProjectTrustAuthorizationResult Failed(string path) =>
        new(
            path,
            CodexProjectTrustLevel.Unknown,
            IsAuthorized: false,
            IsCanceled: false,
            "Could not verify or save project trust. The project was not opened.");
}

public interface IProjectTrustService
{
    Task<ProjectTrustAuthorizationResult> AuthorizeAsync(
        string projectPath,
        CodexInstallation installation,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectTrustService(
    IProjectTrustSession session,
    IUserInteractionService userInteractionService,
    IAppLogger logger) : IProjectTrustService
{
    private readonly SemaphoreSlim trustGate = new(1, 1);

    public async Task<ProjectTrustAuthorizationResult> AuthorizeAsync(
        string projectPath,
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath;
        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "project_trust_path_invalid",
                "The project path could not be normalized for a trust decision.",
                exception: ex);
            return ProjectTrustAuthorizationResult.Failed(projectPath);
        }

        await trustGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await session
                .EnsureProjectTrustSessionConnectedAsync(installation, cancellationToken)
                .ConfigureAwait(true);
            var remembered = await session
                .ReadProjectTrustAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(true);
            if (remembered is CodexProjectTrustLevel.Trusted or CodexProjectTrustLevel.Untrusted)
            {
                return ProjectTrustAuthorizationResult.Authorized(normalizedPath, remembered);
            }

            var decision = userInteractionService.PromptForProjectTrust(normalizedPath);
            if (decision == ProjectTrustDecision.Cancel)
            {
                return ProjectTrustAuthorizationResult.Canceled(normalizedPath);
            }

            var selectedTrust = decision switch
            {
                ProjectTrustDecision.TrustProject => CodexProjectTrustLevel.Trusted,
                ProjectTrustDecision.OpenUntrusted => CodexProjectTrustLevel.Untrusted,
                _ => throw new InvalidOperationException("Unsupported project trust decision.")
            };
            await session
                .WriteProjectTrustAsync(normalizedPath, selectedTrust, cancellationToken)
                .ConfigureAwait(true);
            var persistedTrust = await session
                .ReadProjectTrustAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(true);
            if (persistedTrust != selectedTrust)
            {
                throw new InvalidOperationException("The persisted project trust decision did not match the requested value.");
            }

            return ProjectTrustAuthorizationResult.Authorized(normalizedPath, persistedTrust);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "project_trust_failed",
                "Project activation was denied because its Codex trust state could not be verified.",
                new Dictionary<string, string?> { ["path"] = normalizedPath },
                ex);
            return ProjectTrustAuthorizationResult.Failed(normalizedPath);
        }
        finally
        {
            trustGate.Release();
        }
    }
}
