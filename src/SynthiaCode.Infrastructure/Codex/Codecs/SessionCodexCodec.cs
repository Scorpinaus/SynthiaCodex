using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex.Codecs;

internal sealed class SessionCodexCodec(CodexAppServerClientMetadata metadata)
{
    public CodexRpcCall EncodeInitialize(CodexInitializeOptions options)
    {
        var parameters = new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = metadata.Name,
                ["title"] = metadata.Title,
                ["version"] = metadata.Version
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = options.ExperimentalApi,
                ["optOutNotificationMethods"] = options.OptOutNotificationMethods is null
                    ? null
                    : new JsonArray(options.OptOutNotificationMethods.Select(method => JsonValue.Create(method)).ToArray())
            }
        };

        return new CodexRpcCall("initialize", parameters);
    }

    public CodexAppServerSession DecodeInitialize(JsonNode? response)
    {
        var result = response as JsonObject;
        return new CodexAppServerSession(
            ReadString(result, "userAgent"),
            ReadString(result, "platformFamily"),
            ReadString(result, "platformOs"));
    }

    private static string? ReadString(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
