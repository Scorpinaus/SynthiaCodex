using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex;

internal sealed class CodexNotificationParser(CodexServerRequestParser serverRequestParser)
{
    public bool TryParse(JsonObject message, out CodexParsedNotification parsedNotification)
    {
        var method = message["method"] is JsonValue methodValue &&
            methodValue.TryGetValue<string>(out var methodText)
                ? methodText
                : null;
        if (string.IsNullOrWhiteSpace(method))
        {
            parsedNotification = null!;
            return false;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();
        CodexRequestId? resolvedRequestId = null;
        if (method == CodexAppServerNotificationMethods.ServerRequestResolved &&
            serverRequestParser.TryReadRequestId(
                parameters["requestId"] ?? parameters["id"],
                out var parsedRequestId))
        {
            resolvedRequestId = parsedRequestId;
        }

        parsedNotification = new CodexParsedNotification(
            new AppServerNotification(method, parameters),
            resolvedRequestId);
        return true;
    }
}

internal sealed record CodexParsedNotification(
    AppServerNotification Notification,
    CodexRequestId? ResolvedRequestId);
