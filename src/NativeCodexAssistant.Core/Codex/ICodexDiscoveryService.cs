namespace NativeCodexAssistant.Core.Codex;

public interface ICodexDiscoveryService
{
    Task<CodexInstallation> DetectAsync(
        string? preferredExecutablePath = null,
        CancellationToken cancellationToken = default);
}
