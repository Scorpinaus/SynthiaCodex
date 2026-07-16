using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed class CodexThreadService
{
    public const int MaximumTimelineItems = 500;
    public const int MaximumRawEvents = 500;
    public const int MaximumConversationTurns = 100;
    public const int MaximumPersistedActivityItemsPerTurn = 100;

    private readonly StringBuilder finalResponseBuilder = new();
    private readonly object stateGate = new();

    public ObservableCollection<CodexTimelineItem> TimelineItems { get; } = [];

    public ObservableCollection<string> RawEvents { get; } = [];

    public ObservableCollection<CodexConversationTurn> ConversationTurns { get; } = [];

    public string? ActiveThreadId { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public CodexTurnStatus ActiveTurnStatus { get; private set; } = CodexTurnStatus.Idle;

    public string FinalResponse { get; private set; } = string.Empty;

    public bool RequiresAuthentication { get; private set; }

    public string LastErrorDetail { get; private set; } = string.Empty;

    public CodexConversationTurn? ActiveConversationTurn => ConversationTurns.LastOrDefault(turn =>
        turn.Status == CodexTurnStatus.Running ||
        (!string.IsNullOrWhiteSpace(ActiveTurnId) && string.Equals(turn.TurnId, ActiveTurnId, StringComparison.Ordinal)));

    public void Reset()
    {
        TimelineItems.Clear();
        RawEvents.Clear();
        ConversationTurns.Clear();
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
        IEnumerable<string>? rawEvents,
        string? legacyPrompt = null,
        IEnumerable<CodexConversationTurnSnapshot>? conversationTurns = null)
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
                AddBounded(TimelineItems, item, MaximumTimelineItems);
            }
        }

        if (rawEvents is not null)
        {
            foreach (var rawEvent in rawEvents)
            {
                AddBounded(RawEvents, rawEvent, MaximumRawEvents);
            }
        }

        if (conversationTurns is not null)
        {
            foreach (var snapshot in conversationTurns.TakeLast(MaximumConversationTurns))
            {
                ConversationTurns.Add(CodexConversationTurn.FromSnapshot(snapshot));
            }
        }

        if (ConversationTurns.Count == 0 &&
            (!string.IsNullOrWhiteSpace(legacyPrompt) || !string.IsNullOrWhiteSpace(finalResponse)))
        {
            var legacyTurn = new CodexConversationTurn
            {
                UserPrompt = legacyPrompt ?? string.Empty,
                AssistantResponse = finalResponse ?? string.Empty,
                Status = CodexTurnStatus.Completed
            };
            foreach (var item in TimelineItems)
            {
                legacyTurn.Activity.Add(item);
            }
            ConversationTurns.Add(legacyTurn);
        }

        RefreshCompatibilityResponse();
    }

    public CodexConversationTurn BeginTurn(string prompt)
    {
        lock (stateGate)
        {
            var turn = new CodexConversationTurn
            {
                UserPrompt = prompt,
                Status = CodexTurnStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            };
            AddBounded(ConversationTurns, turn, MaximumConversationTurns);
            ActiveTurnId = null;
            ActiveTurnStatus = CodexTurnStatus.Running;
            finalResponseBuilder.Clear();
            FinalResponse = string.Empty;
            return turn;
        }
    }

    public CodexConversationTurn BindPendingTurn(string turnId)
    {
        lock (stateGate)
        {
            var existing = ConversationTurns.FirstOrDefault(item =>
                string.Equals(item.TurnId, turnId, StringComparison.Ordinal));
            var pending = ConversationTurns.LastOrDefault(item =>
                item.Status == CodexTurnStatus.Running && string.IsNullOrWhiteSpace(item.TurnId));
            var turn = existing ?? pending ?? GetOrCreateTurn(turnId);
            if (existing is not null && pending is not null && !ReferenceEquals(existing, pending))
            {
                if (string.IsNullOrWhiteSpace(existing.UserPrompt))
                {
                    existing.UserPrompt = pending.UserPrompt;
                }
                foreach (var item in pending.Activity)
                {
                    AddBounded(existing.Activity, item, MaximumTimelineItems);
                }
                ConversationTurns.Remove(pending);
            }
            turn.TurnId = turnId;
            if (turn.Status == CodexTurnStatus.Idle)
            {
                turn.Status = CodexTurnStatus.Running;
            }
            ActiveTurnId = turnId;
            ActiveTurnStatus = turn.Status;
            return turn;
        }
    }

    public void FailPendingTurn(string detail)
    {
        var turn = ConversationTurns.LastOrDefault(item => item.Status == CodexTurnStatus.Running);
        if (turn is null)
        {
            return;
        }

        turn.Status = CodexTurnStatus.Failed;
        turn.CompletedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(turn.AssistantResponse))
        {
            turn.AssistantResponse = detail;
        }
        ActiveTurnStatus = CodexTurnStatus.Failed;
        ActiveTurnId = null;
        RefreshCompatibilityResponse();
    }

    public void AddGuidance(string guidance)
    {
        if (ActiveConversationTurn is not { } turn || string.IsNullOrWhiteSpace(guidance))
        {
            return;
        }

        var item = new CodexTimelineItem(
            CodexTimelineItemKind.UserGuidance,
            "Guidance added",
            guidance,
            "turn/steer",
            DateTimeOffset.Now);
        AddBounded(turn.Activity, item, MaximumTimelineItems);
        AddBounded(TimelineItems, item, MaximumTimelineItems);
    }

    public void ReconcileHistory(IEnumerable<CodexConversationTurnSnapshot> history)
    {
        var snapshots = history.TakeLast(MaximumConversationTurns).ToList();
        if (snapshots.Count == 0)
        {
            return;
        }

        if (ConversationTurns.Count == 1 && string.IsNullOrWhiteSpace(ConversationTurns[0].TurnId))
        {
            ConversationTurns.Clear();
        }

        foreach (var snapshot in snapshots)
        {
            var turn = ConversationTurns.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(snapshot.TurnId) &&
                string.Equals(item.TurnId, snapshot.TurnId, StringComparison.Ordinal));
            if (turn is null)
            {
                ConversationTurns.Add(CodexConversationTurn.FromSnapshot(snapshot));
                continue;
            }

            turn.UserPrompt = snapshot.UserPrompt;
            turn.AssistantResponse = snapshot.AssistantResponse;
            turn.Status = snapshot.Status;
            turn.StartedAt = snapshot.StartedAt;
            turn.CompletedAt = snapshot.CompletedAt;
        }

        while (ConversationTurns.Count > MaximumConversationTurns)
        {
            ConversationTurns.RemoveAt(0);
        }
        RefreshCompatibilityResponse();
    }

    public IReadOnlyList<CodexConversationTurnSnapshot> SnapshotConversation() =>
        ConversationTurns.TakeLast(MaximumConversationTurns).Select(turn =>
        {
            var snapshot = turn.ToSnapshot();
            snapshot.Activity = [.. snapshot.Activity.TakeLast(MaximumPersistedActivityItemsPerTurn)];
            return snapshot;
        }).ToList();

    public void ApplyNotification(AppServerNotification notification)
    {
        lock (stateGate)
        {
            AddBounded(
                RawEvents,
                $"{notification.Method}: {notification.Params.ToJsonString()}",
                MaximumRawEvents);
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
                if (!string.IsNullOrWhiteSpace(ActiveTurnId))
                {
                    BindPendingTurn(ActiveTurnId);
                }
                Add(CodexTimelineItemKind.TurnStarted, "Turn started", ActiveTurnId, notification);
                break;

            case "item/agentMessage/delta":
                var delta = ReadString(notification.Params, "delta") ?? ReadString(notification.Params, "text") ?? string.Empty;
                if (!string.IsNullOrEmpty(delta))
                {
                    var responseTurn = GetNotificationTurn(notification);
                    if (responseTurn is not null)
                    {
                        responseTurn.AssistantResponse += delta;
                    }
                    finalResponseBuilder.Append(delta);
                    FinalResponse = finalResponseBuilder.ToString();
                }

                Add(CodexTimelineItemKind.AgentMessageDelta, "Agent message", delta, notification);
                break;

            case "turn/completed":
                ActiveTurnId ??= ReadString(notification.Params, "turn.id");
                ActiveTurnStatus = ReadTurnStatus(ReadString(notification.Params, "status") ?? ReadString(notification.Params, "turn.status"));
                var completedTurn = GetNotificationTurn(notification);
                if (completedTurn is not null)
                {
                    completedTurn.Status = ActiveTurnStatus;
                    completedTurn.CompletedAt = DateTimeOffset.UtcNow;
                }
                Add(CodexTimelineItemKind.TurnCompleted, "Turn completed", ReadTurnCompletedDetail(notification.Params), notification);
                RefreshCompatibilityResponse();
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
            var responseTurn = GetNotificationTurn(notification);
            if (responseTurn is not null)
            {
                responseTurn.AssistantResponse = agentText;
            }
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
        var item = new CodexTimelineItem(
                kind,
                title,
                detail ?? string.Empty,
                notification.Method,
                DateTimeOffset.Now);
        AddBounded(TimelineItems, item, MaximumTimelineItems);

        var itemType = ReadString(notification.Params, "item.type");
        if (kind is not (CodexTimelineItemKind.AgentMessage or CodexTimelineItemKind.AgentMessageDelta) &&
            itemType is not ("userMessage" or "user_message" or "agentMessage" or "agent_message") &&
            GetNotificationTurn(notification) is { } turn)
        {
            AddBounded(turn.Activity, item, MaximumTimelineItems);
        }
    }

    private static void AddBounded<T>(ObservableCollection<T> collection, T item, int maximumCount)
    {
        while (collection.Count >= maximumCount)
        {
            collection.RemoveAt(0);
        }

        collection.Add(item);
    }

    private CodexConversationTurn GetOrCreateTurn(string turnId)
    {
        var existing = ConversationTurns.FirstOrDefault(turn =>
            string.Equals(turn.TurnId, turnId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var pending = ConversationTurns.LastOrDefault(turn =>
            turn.Status == CodexTurnStatus.Running && string.IsNullOrWhiteSpace(turn.TurnId));
        if (pending is not null)
        {
            pending.TurnId = turnId;
            return pending;
        }

        var created = new CodexConversationTurn
        {
            TurnId = turnId,
            Status = CodexTurnStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        AddBounded(ConversationTurns, created, MaximumConversationTurns);
        return created;
    }

    private CodexConversationTurn? GetNotificationTurn(AppServerNotification notification)
    {
        var turnId = ReadString(notification.Params, "turnId") ?? ReadString(notification.Params, "turn.id");
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            return GetOrCreateTurn(turnId);
        }

        return ActiveConversationTurn;
    }

    private void RefreshCompatibilityResponse()
    {
        FinalResponse = ConversationTurns.LastOrDefault()?.AssistantResponse ?? FinalResponse;
        finalResponseBuilder.Clear();
        finalResponseBuilder.Append(FinalResponse);
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
    Raw,
    UserGuidance
}

public enum CodexTurnStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled
}
