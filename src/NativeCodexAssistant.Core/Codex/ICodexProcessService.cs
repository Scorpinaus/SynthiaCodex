using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.Core.Codex;

public interface ICodexProcessService
{
    Task<IAppServerTransport> StartAppServerTransportAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);
}
