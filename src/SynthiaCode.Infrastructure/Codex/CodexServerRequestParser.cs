using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex;

internal sealed class CodexServerRequestParser
{
    public bool TryParse(
        string method,
        JsonObject parameters,
        CodexRequestId requestId,
        out CodexServerRequest request,
        out string? error)
    {
        error = null;
        CodexServerRequestPayload payload;
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                if (!TryReadApprovalCorrelation(parameters, out var commandThreadId, out var commandTurnId, out var commandItemId, out var commandStartedAt, out error))
                {
                    request = null!;
                    return false;
                }

                CodexNetworkApprovalContext? networkContext = null;
                if (parameters["networkApprovalContext"] is JsonObject network)
                {
                    var host = ReadString(network, "host");
                    var protocol = ReadString(network, "protocol");
                    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(protocol))
                    {
                        networkContext = new CodexNetworkApprovalContext(host, protocol, ReadInt(network, "port"));
                    }
                }

                payload = new CodexCommandApprovalRequest(
                    commandThreadId,
                    commandTurnId,
                    commandItemId,
                    commandStartedAt,
                    ReadString(parameters, "command"),
                    ReadString(parameters, "cwd"),
                    ReadString(parameters, "reason"),
                    networkContext,
                    ReadStringArray(parameters, "proposedExecpolicyAmendment"),
                    ReadStringArray(parameters, "availableDecisions"),
                    ReadString(parameters, "approvalId"));
                break;

            case "item/fileChange/requestApproval":
                if (!TryReadApprovalCorrelation(parameters, out var fileThreadId, out var fileTurnId, out var fileItemId, out var fileStartedAt, out error))
                {
                    request = null!;
                    return false;
                }

                payload = new CodexFileChangeApprovalRequest(
                    fileThreadId,
                    fileTurnId,
                    fileItemId,
                    fileStartedAt,
                    ReadString(parameters, "reason"),
                    ReadString(parameters, "grantRoot"));
                break;

            case "item/permissions/requestApproval":
                if (!TryReadApprovalCorrelation(parameters, out var permissionThreadId, out var permissionTurnId, out var permissionItemId, out var permissionStartedAt, out error))
                {
                    request = null!;
                    return false;
                }

                var cwd = ReadString(parameters, "cwd");
                var permissions = parameters["permissions"] as JsonObject;
                if (string.IsNullOrWhiteSpace(cwd) || permissions is null)
                {
                    request = null!;
                    error = "Permission approval requires cwd and permissions.";
                    return false;
                }

                payload = new CodexPermissionApprovalRequest(
                    permissionThreadId,
                    permissionTurnId,
                    permissionItemId,
                    permissionStartedAt,
                    cwd,
                    ReadString(parameters, "reason"),
                    (JsonObject)permissions.DeepClone());
                break;

            default:
                payload = new CodexUnsupportedServerRequest(method);
                break;
        }

        request = new CodexServerRequest(
            requestId,
            method,
            (JsonObject)parameters.DeepClone(),
            payload);
        return true;
    }

    public bool TryReadRequestId(JsonNode? value, out CodexRequestId requestId)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var integer))
        {
            requestId = CodexRequestId.FromInteger(integer);
            return true;
        }

        if (value is JsonValue stringValue && stringValue.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            requestId = CodexRequestId.FromString(text);
            return true;
        }

        requestId = default;
        return false;
    }

    private static bool TryReadApprovalCorrelation(
        JsonObject parameters,
        out string threadId,
        out string turnId,
        out string itemId,
        out long startedAtMs,
        out string? error)
    {
        threadId = ReadString(parameters, "threadId") ?? string.Empty;
        turnId = ReadString(parameters, "turnId") ?? string.Empty;
        itemId = ReadString(parameters, "itemId") ?? string.Empty;
        var parsedStartedAt = ReadLong(parameters, "startedAtMs");
        startedAtMs = parsedStartedAt ?? 0;
        if (string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(turnId) ||
            string.IsNullOrWhiteSpace(itemId) ||
            parsedStartedAt is null)
        {
            error = "Approval request is missing threadId, turnId, itemId, or startedAtMs.";
            return false;
        }

        error = null;
        return true;
    }

    private static string? ReadString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int? ReadInt(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static long? ReadLong(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value && value.TryGetValue<long>(out var number)
            ? number
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject source, string propertyName) =>
        source[propertyName] is JsonArray values
            ? values
                .Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray()
            : [];
}
