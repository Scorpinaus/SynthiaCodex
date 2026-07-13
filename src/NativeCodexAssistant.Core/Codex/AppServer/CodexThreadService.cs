using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed class CodexThreadService
{
    private readonly StringBuilder finalResponseBuilder = new();

    public ObservableCollection<CodexTimelineItem> TimelineItems { get; } = [];

    public ObservableCollection<string> RawEvents { get; } = [];

    public string? ActiveThreadId { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public CodexTurnStatus ActiveTurnStatus { get; private set; } = CodexTurnStatus.Idle;

    public string FinalResponse { get; private set; } = string.Empty;

    public bool RequiresAuthentication { get; private set; }

    public string LastErrorDetail { get; private set; } = string.Empty;

    public void Reset()
    {
        TimelineItems.Clear();
        RawEvents.Clear();
        ActiveThreadId = null;
        ActiveTurnId = null;
        ActiveTurnStatus = CodexTurnStatus.Idle;
        FinalResponse = string.Empty;
        RequiresAuthentication = false;
        LastErrorDetail = string.Empty;
        finalResponseBuilder.Clear();
    }

    public void Restore(
        string? threadId,
        string? finalResponse,
        IEnumerable<CodexTimelineItem>? timelineItems,
        IEnumerable<string>? rawEvents)
    {
        Reset();
        ActiveThreadId = threadId;
        ActiveTurnStatus = CodexTurnStatus.Idle;

        if (!string.IsNullOrWhiteSpace(finalResponse))
        {
            finalResponseBuilder.Append(finalResponse);
            FinalResponse = finalResponse;
        }

        if (timelineItems is not null)
        {
            foreach (var item in timelineItems)
            {
                TimelineItems.Add(item);
            }
        }

        if (rawEvents is not null)
        {
            foreach (var rawEvent in rawEvents)
            {
                RawEvents.Add(rawEvent);
            }
        }
    }

    public void ApplyNotification(AppServerNotification notification)
    {
        RawEvents.Add($"{notification.Method}: {notification.Params.ToJsonString()}");
        CaptureErrorState(notification.Params);

        switch (notification.Method)
        {
            case "thread/started":
                ActiveThreadId = ReadString(notification.Params, "thread.id");
                Add(CodexTimelineItemKind.ThreadStarted, "Thread started", ActiveThreadId, notification);
                break;

            case "turn/started":
                ActiveTurnId = ReadString(notification.Params, "turn.id");
                ActiveTurnStatus = CodexTurnStatus.Running;
                Add(CodexTimelineItemKind.TurnStarted, "Turn started", ActiveTurnId, notification);
                break;

            case "item/agentMessage/delta":
                var delta = ReadString(notification.Params, "delta") ?? ReadString(notification.Params, "text") ?? string.Empty;
                if (!string.IsNullOrEmpty(delta))
                {
                    finalResponseBuilder.Append(delta);
                    FinalResponse = finalResponseBuilder.ToString();
                }

                Add(CodexTimelineItemKind.AgentMessageDelta, "Agent message", delta, notification);
                break;

            case "turn/completed":
                ActiveTurnId ??= ReadString(notification.Params, "turn.id");
                ActiveTurnStatus = ReadTurnStatus(ReadString(notification.Params, "status") ?? ReadString(notification.Params, "turn.status"));
                Add(CodexTimelineItemKind.TurnCompleted, "Turn completed", ReadTurnCompletedDetail(notification.Params), notification);
                break;

            case "item/started":
                Add(ReadItemStartedKind(notification.Params), "Item started", ReadItemDetail(notification.Params), notification);
                break;

            case "item/completed":
                ApplyCompletedItem(notification);
                break;

            default:
                Add(ReadFallbackKind(notification), notification.Method, ReadItemDetail(notification.Params), notification);
                break;
        }
    }

    private void ApplyCompletedItem(AppServerNotification notification)
    {
        var kind = ReadItemCompletedKind(notification.Params);
        var detail = ReadItemDetail(notification.Params);
        var agentText = ReadString(notification.Params, "item.text") ??
            ReadString(notification.Params, "item.message") ??
            ReadString(notification.Params, "item.content");

        if (kind == CodexTimelineItemKind.AgentMessage && !string.IsNullOrWhiteSpace(agentText))
        {
            finalResponseBuilder.Clear();
            finalResponseBuilder.Append(agentText);
            FinalResponse = finalResponseBuilder.ToString();
        }

        Add(kind, "Item completed", detail, notification);
    }

    private static CodexTimelineItemKind ReadItemStartedKind(JsonObject parameters)
    {
        return ReadString(parameters, "item.type") switch
        {
            "command" or "command_execution" or "exec" => CodexTimelineItemKind.CommandStarted,
            "commandExecution" => CodexTimelineItemKind.CommandStarted,
            "tool" or "tool_call" => CodexTimelineItemKind.ToolProgress,
            "mcpToolCall" or "dynamicToolCall" => CodexTimelineItemKind.ToolProgress,
            _ => CodexTimelineItemKind.Raw
        };
    }

    private static CodexTimelineItemKind ReadItemCompletedKind(JsonObject parameters)
    {
        return ReadString(parameters, "item.type") switch
        {
            "command" or "command_execution" or "exec" => CodexTimelineItemKind.CommandCompleted,
            "commandExecution" => CodexTimelineItemKind.CommandCompleted,
            "file_change" or "file" or "patch" => CodexTimelineItemKind.FileChange,
            "fileChange" => CodexTimelineItemKind.FileChange,
            "agent_message" or "message" => CodexTimelineItemKind.AgentMessage,
            "agentMessage" => CodexTimelineItemKind.AgentMessage,
            "tool" or "tool_call" => CodexTimelineItemKind.ToolProgress,
            "mcpToolCall" or "dynamicToolCall" => CodexTimelineItemKind.ToolProgress,
            _ => CodexTimelineItemKind.Raw
        };
    }

    private static CodexTimelineItemKind ReadFallbackKind(AppServerNotification notification)
    {
        if (notification.Method.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            notification.Params["error"] is not null)
        {
            return CodexTimelineItemKind.Error;
        }

        if (notification.Method.Contains("progress", StringComparison.OrdinalIgnoreCase))
        {
            return CodexTimelineItemKind.ToolProgress;
        }

        return CodexTimelineItemKind.Raw;
    }

    private static string? ReadItemDetail(JsonObject parameters)
    {
        var primary = ReadString(parameters, "item.command") ??
            ReadString(parameters, "item.text") ??
            ReadString(parameters, "item.path") ??
            ReadString(parameters, "item.name") ??
            ReadString(parameters, "item.aggregatedOutput") ??
            ReadString(parameters, "error.message") ??
            ReadString(parameters, "message") ??
            ReadString(parameters, "status");
        var additional = ReadString(parameters, "error.additionalDetails") ??
            ReadString(parameters, "additionalDetails");

        if (!string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(additional))
        {
            return $"{primary} {additional}";
        }

        return primary ?? additional;
    }

    private static string? ReadTurnCompletedDetail(JsonObject parameters)
    {
        var status = ReadString(parameters, "status") ?? ReadString(parameters, "turn.status");
        var error = ReadString(parameters, "turn.error.message") ?? ReadString(parameters, "error.message");
        var additional = ReadString(parameters, "turn.error.additionalDetails") ??
            ReadString(parameters, "error.additionalDetails");

        if (!string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(additional))
        {
            return $"{status}: {error} {additional}";
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return string.IsNullOrWhiteSpace(status) ? error : $"{status}: {error}";
        }

        return status;
    }

    private void CaptureErrorState(JsonObject parameters)
    {
        var detail = ReadItemDetail(parameters) ?? ReadTurnCompletedDetail(parameters);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            LastErrorDetail = detail;
        }

        var statusCode = ReadInt(parameters, "error.codexErrorInfo.responseStreamDisconnected.httpStatusCode") ??
            ReadInt(parameters, "turn.error.codexErrorInfo.responseStreamDisconnected.httpStatusCode") ??
            ReadInt(parameters, "turn.error.codexErrorInfo.httpConnectionFailed.httpStatusCode");
        var codexErrorInfo = ReadString(parameters, "turn.error.codexErrorInfo") ??
            ReadString(parameters, "error.codexErrorInfo");

        if (statusCode == 401 ||
            ContainsAuthFailureText(detail) ||
            ContainsAuthFailureText(codexErrorInfo))
        {
            RequiresAuthentication = true;
        }
    }

    private static bool ContainsAuthFailureText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            (value.Contains("401", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("missing bearer", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("authentication required", StringComparison.OrdinalIgnoreCase));
    }

    private static CodexTurnStatus ReadTurnStatus(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "completed" or "success" or "succeeded" => CodexTurnStatus.Completed,
            "cancelled" or "canceled" or "interrupted" => CodexTurnStatus.Cancelled,
            "failed" or "error" => CodexTurnStatus.Failed,
            _ => CodexTurnStatus.Completed
        };
    }

    private void Add(CodexTimelineItemKind kind, string title, string? detail, AppServerNotification notification)
    {
        TimelineItems.Add(new CodexTimelineItem(
            kind,
            title,
            detail ?? string.Empty,
            notification.Method,
            DateTimeOffset.Now));
    }

    private static string? ReadString(JsonObject obj, string path)
    {
        JsonNode? current = obj;
        foreach (var segment in path.Split('.'))
        {
            if (current is JsonObject currentObject)
            {
                current = currentObject[segment];
                continue;
            }

            if (current is JsonArray currentArray && int.TryParse(segment, out var index))
            {
                current = index >= 0 && index < currentArray.Count ? currentArray[index] : null;
                continue;
            }

            return null;
        }

        if (current is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    private static int? ReadInt(JsonObject obj, string path)
    {
        JsonNode? current = obj;
        foreach (var segment in path.Split('.'))
        {
            if (current is JsonObject currentObject)
            {
                current = currentObject[segment];
                continue;
            }

            if (current is JsonArray currentArray && int.TryParse(segment, out var index))
            {
                current = index >= 0 && index < currentArray.Count ? currentArray[index] : null;
                continue;
            }

            return null;
        }

        if (current is null)
        {
            return null;
        }

        if (current is JsonValue value && value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (current is JsonValue stringValue &&
            stringValue.TryGetValue<string>(out var textValue) &&
            int.TryParse(textValue, out var parsedValue))
        {
            return parsedValue;
        }

        return null;
    }
}

public sealed record CodexTimelineItem(
    CodexTimelineItemKind Kind,
    string Title,
    string Detail,
    string Method,
    DateTimeOffset Timestamp);

public enum CodexTimelineItemKind
{
    ThreadStarted,
    TurnStarted,
    CommandStarted,
    CommandCompleted,
    FileChange,
    AgentMessage,
    AgentMessageDelta,
    ToolProgress,
    Error,
    TurnCompleted,
    Raw
}

public enum CodexTurnStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled
}
