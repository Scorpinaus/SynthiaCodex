using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex.Codecs;

internal sealed class AccountCodexCodec
{
    public CodexRpcCall EncodeRead(bool refreshToken) =>
        new(
            "account/read",
            new JsonObject
            {
                ["refreshToken"] = refreshToken
            });

    public CodexAccountReadResult DecodeRead(JsonNode? response) =>
        CodexAccountProtocolParser.ParseAccount(response as JsonObject);

    public CodexRpcCall EncodeReadRateLimits() =>
        new("account/rateLimits/read");

    public CodexAccountRateLimitsResult DecodeReadRateLimits(JsonNode? response) =>
        CodexAccountProtocolParser.ParseRateLimits(response as JsonObject);
}
