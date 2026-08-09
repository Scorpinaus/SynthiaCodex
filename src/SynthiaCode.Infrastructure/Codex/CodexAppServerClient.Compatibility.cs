using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex;

public sealed class CodexAppServerClient(
    IAppServerTransport transport,
    CodexAppServerClientMetadata metadata)
    : CodexClient(transport, metadata);
