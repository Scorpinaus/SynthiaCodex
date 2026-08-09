using System.Text.Json.Nodes;

namespace SynthiaCode.Infrastructure.Codex.Codecs;

internal sealed record CodexRpcCall(string Method, JsonObject? Parameters = null);
