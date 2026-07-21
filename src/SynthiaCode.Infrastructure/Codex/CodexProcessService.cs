using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Codex;

public sealed class CodexProcessService(
    IAppLogger logger,
    CodexRuntimeEnvironment? runtimeEnvironment = null) : ICodexProcessService
{
    public async Task<IAppServerTransport> StartAppServerTransportAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        if (!installation.IsFound || string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            throw new InvalidOperationException("Codex CLI is not available.");
        }

        var transport = new CodexAppServerProcessTransport(
            installation.ExecutablePath,
            logger,
            runtimeEnvironment);
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        return transport;
    }
}
