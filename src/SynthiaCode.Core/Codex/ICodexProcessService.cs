using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Core.Codex;

public interface ICodexProcessService
{
    Task<IAppServerTransport> StartAppServerTransportAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);
}
