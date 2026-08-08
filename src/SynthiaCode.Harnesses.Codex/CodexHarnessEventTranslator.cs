using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Harnesses.Codex;

public static class CodexHarnessEventTranslator
{
    public static IReadOnlyList<HarnessEvent> Translate(
        CodexAppServerNotification notification,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var occurredAt = timestamp ?? DateTimeOffset.UtcNow;
        var threadId = notification.ThreadId;
        var turnId = notification.TurnId;

        return notification.Kind switch
        {
            CodexAppServerNotificationKind.ThreadStarted when threadId is not null =>
                [new ConversationStartedEvent(HarnessId.Codex, threadId, occurredAt)],
            CodexAppServerNotificationKind.ThreadArchived when threadId is not null =>
                [new ConversationArchivedEvent(HarnessId.Codex, threadId, true, occurredAt)],
            CodexAppServerNotificationKind.ThreadUnarchived when threadId is not null =>
                [new ConversationArchivedEvent(HarnessId.Codex, threadId, false, occurredAt)],
            CodexAppServerNotificationKind.TurnStarted when threadId is not null && turnId is not null =>
                [new TurnStartedEvent(HarnessId.Codex, threadId, turnId, occurredAt)],
            CodexAppServerNotificationKind.AgentMessageDelta
                when threadId is not null && turnId is not null =>
                TranslateAgentDelta(notification, threadId, turnId, occurredAt),
            CodexAppServerNotificationKind.TurnDiffUpdated
                when threadId is not null && turnId is not null =>
                [new TurnDiffChangedEvent(
                    HarnessId.Codex,
                    threadId,
                    turnId,
                    ReadString(notification.Params, "diff") ?? string.Empty,
                    occurredAt)],
            CodexAppServerNotificationKind.TurnCompleted
                when threadId is not null && turnId is not null =>
                [new TurnCompletedEvent(
                    HarnessId.Codex,
                    threadId,
                    turnId,
                    ParseStatus(notification.TurnStatus),
                    ReadString(notification.Params, "turn.error.message") ?? ReadString(notification.Params, "error.message"),
                    occurredAt)],
            CodexAppServerNotificationKind.ThreadTokenUsageUpdated when threadId is not null =>
                TranslateUsage(notification.Params, threadId, occurredAt),
            CodexAppServerNotificationKind.ThreadCompacted when threadId is not null =>
                [new ContextCompactedEvent(HarnessId.Codex, threadId, turnId, occurredAt)],
            CodexAppServerNotificationKind.ItemCompleted
                when threadId is not null && turnId is not null &&
                     ReadString(notification.Params, "item.type") == "agentMessage" =>
                TranslateCompletedAgentMessage(notification, threadId, turnId, occurredAt),
            CodexAppServerNotificationKind.ItemStarted or
            CodexAppServerNotificationKind.ItemCompleted or
            CodexAppServerNotificationKind.McpToolCallProgress or
            CodexAppServerNotificationKind.TurnPlanUpdated
                when threadId is not null =>
                TranslateActivity(notification, threadId, turnId, occurredAt),
            _ => []
        };
    }

    private static IReadOnlyList<HarnessEvent> TranslateAgentDelta(
        CodexAppServerNotification notification,
        string threadId,
        string turnId,
        DateTimeOffset timestamp)
    {
        var delta = ReadString(notification.Params, "delta") ?? ReadString(notification.Params, "text");
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        return [new AssistantTextDeltaEvent(
            HarnessId.Codex,
            threadId,
            turnId,
            notification.ItemId ?? "legacy-agent-message",
            delta,
            timestamp)];
    }

    private static IReadOnlyList<HarnessEvent> TranslateCompletedAgentMessage(
        CodexAppServerNotification notification,
        string threadId,
        string turnId,
        DateTimeOffset timestamp) =>
        [new AssistantMessageCompletedEvent(
            HarnessId.Codex,
            threadId,
            turnId,
            notification.ItemId ?? "legacy-agent-message",
            ReadString(notification.Params, "item.text") ??
                ReadString(notification.Params, "item.message") ??
                ReadString(notification.Params, "item.content") ??
                string.Empty,
            ReadString(notification.Params, "item.phase"),
            timestamp)];

    private static IReadOnlyList<HarnessEvent> TranslateUsage(
        JsonObject parameters,
        string threadId,
        DateTimeOffset timestamp)
    {
        var total = ReadLong(parameters, "tokenUsage.last.totalTokens");
        var reasoning = ReadLong(parameters, "tokenUsage.last.reasoningOutputTokens") ?? 0;
        var window = ReadLong(parameters, "tokenUsage.modelContextWindow");
        if (total is null or < 0 || reasoning < 0 || window is null or <= 0)
        {
            return [];
        }

        return [new ContextUsageChangedEvent(
            HarnessId.Codex,
            threadId,
            total.Value - Math.Min(total.Value, reasoning),
            window.Value,
            timestamp)];
    }

    private static IReadOnlyList<HarnessEvent> TranslateActivity(
        CodexAppServerNotification notification,
        string threadId,
        string? turnId,
        DateTimeOffset timestamp)
    {
        var itemType = ReadString(notification.Params, "item.type") ?? notification.Method;
        if (itemType is "enteredReviewMode" or "exitedReviewMode")
        {
            // These carry protocol-specific review scope/findings and are projected
            // directly by the application instead of as generic tool activity.
            return [];
        }

        var detail = ReadString(notification.Params, "item.text") ??
            ReadString(notification.Params, "item.command") ??
            ReadString(notification.Params, "item.query") ??
            ReadString(notification.Params, "message") ??
            itemType;
        var completed = notification.Kind == CodexAppServerNotificationKind.ItemCompleted;
        var activity = new ActivityItem(
            notification.ItemId ?? $"{notification.Method}:{turnId ?? threadId}",
            GetActivityKind(notification, itemType),
            GetActivityTitle(notification, itemType),
            detail,
            timestamp,
            completed,
            false);
        return [new ActivityChangedEvent(
            HarnessId.Codex,
            threadId,
            turnId,
            activity,
            timestamp)];
    }

    private static ActivityKind GetActivityKind(
        CodexAppServerNotification notification,
        string itemType)
    {
        if (notification.Kind == CodexAppServerNotificationKind.TurnPlanUpdated)
        {
            return ActivityKind.Plan;
        }
        if (notification.Kind == CodexAppServerNotificationKind.McpToolCallProgress)
        {
            return ActivityKind.Tool;
        }

        return itemType.Trim().ToLowerInvariant() switch
        {
            "commandexecution" or "command_execution" or "command" => ActivityKind.Command,
            "filechange" or "file_change" => ActivityKind.FileChange,
            "websearch" or "web_search" => ActivityKind.WebSearch,
            "imagegeneration" or "image_generation" => ActivityKind.ImageGeneration,
            "collaborationtoolcall" or "collaboration_tool_call" => ActivityKind.Collaboration,
            "reasoning" => ActivityKind.Reasoning,
            "plan" => ActivityKind.Plan,
            _ => ActivityKind.Tool
        };
    }

    private static string GetActivityTitle(
        CodexAppServerNotification notification,
        string itemType) => notification.Kind switch
    {
        CodexAppServerNotificationKind.TurnPlanUpdated => "Plan updated",
        CodexAppServerNotificationKind.McpToolCallProgress => "Tool progress",
        CodexAppServerNotificationKind.ItemCompleted => $"{itemType} completed",
        _ => $"{itemType} started"
    };

    private static ConversationTurnStatus ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "completed" or "success" or "succeeded" => ConversationTurnStatus.Completed,
        "cancelled" or "canceled" or "aborted" => ConversationTurnStatus.Cancelled,
        "running" or "inprogress" or "in_progress" => ConversationTurnStatus.Running,
        "failed" or "error" => ConversationTurnStatus.Failed,
        _ => ConversationTurnStatus.Failed
    };

    private static string? ReadString(JsonObject parameters, string path) =>
        ReadNode(parameters, path) is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static long? ReadLong(JsonObject parameters, string path)
    {
        if (ReadNode(parameters, path) is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<long>(out var integer))
        {
            return integer;
        }
        return value.TryGetValue<double>(out var number) && number >= long.MinValue && number <= long.MaxValue
            ? (long)number
            : null;
    }

    private static JsonNode? ReadNode(JsonObject parameters, string path)
    {
        JsonNode? current = parameters;
        foreach (var segment in path.Split('.'))
        {
            current = current is JsonObject currentObject ? currentObject[segment] : null;
        }
        return current;
    }
}
