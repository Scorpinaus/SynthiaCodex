using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Logging;

namespace NativeCodexAssistant.Infrastructure.Codex;

public sealed class CodexProcessService(IAppLogger logger) : ICodexProcessService
{
    public async Task<IAppServerTransport> StartAppServerTransportAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        if (!installation.IsFound || string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            throw new InvalidOperationException("Codex CLI is not available.");
        }

        var transport = new CodexAppServerProcessTransport(installation.ExecutablePath, logger);
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        return transport;
    }
}
