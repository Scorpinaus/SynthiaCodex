using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed class CodexThreadService
{
    public const int MaximumTimelineItems = 500;
    public const int MaximumRawEvents = 500;
    public const int MaximumConversationTurns = 100;
    public const int MaximumPersistedActivityItemsPerTurn = 100;

    private readonly StringBuilder finalResponseBuilder = new();
    private readonly object stateGate = new();
    private readonly Dictionary<string, AgentMessageState> agentMessages = [];
    private readonly Dictionary<CodexConversationTurn, string> responsePrefixes = [];
    private long nextAgentMessageSequence;

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
        agentMessages.Clear();
        responsePrefixes.Clear();
        nextAgentMessageSequence = 0;
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
            var repairedFinalResponse = UnicodeTextNormalizer.RepairLegacyMojibake(finalResponse);
            finalResponseBuilder.Append(repairedFinalResponse);
            FinalResponse = repairedFinalResponse;
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
                var turn = CodexConversationTurn.FromSnapshot(snapshot);
                SanitizeRestoredActivity(turn);
                ConversationTurns.Add(turn);
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
                if (IsUserFacingLegacyActivity(item))
                {
                    legacyTurn.Activity.Add(UnicodeTextNormalizer.RepairLegacyMojibake(item));
                }
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
            DateTimeOffset.Now)
        {
            ActivityKey = $"guidance:{Guid.NewGuid():N}"
        };
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
            turn.AssistantResponse = UnicodeTextNormalizer.RepairLegacyMojibake(snapshot.AssistantResponse);
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
                AddTimeline(CodexTimelineItemKind.ThreadStarted, "Thread started", ActiveThreadId, notification);
                break;

            case "turn/started":
                ActiveTurnId = ReadString(notification.Params, "turn.id");
                ActiveTurnStatus = CodexTurnStatus.Running;
                if (!string.IsNullOrWhiteSpace(ActiveTurnId))
                {
                    BindPendingTurn(ActiveTurnId);
                }
                AddTimeline(CodexTimelineItemKind.TurnStarted, "Turn started", ActiveTurnId, notification);
                break;

            case "item/agentMessage/delta":
                ApplyAgentMessageDelta(notification);
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
                AddTimeline(CodexTimelineItemKind.TurnCompleted, "Turn completed", ReadTurnCompletedDetail(notification.Params), notification);
                if (ActiveTurnStatus == CodexTurnStatus.Failed)
                {
                    ProjectStandaloneError(notification, ReadTurnCompletedDetail(notification.Params));
                }
                RefreshCompatibilityResponse();
                if (completedTurn is not null)
                {
                    ReleaseAgentMessageState(completedTurn);
                }
                break;

            case "item/started":
                RememberAgentMessagePhase(notification);
                AddTimeline(ReadItemStartedKind(notification.Params), "Item started", ReadItemDetail(notification.Params), notification);
                ProjectItemActivity(notification, completed: false);
                break;

            case "item/completed":
                ApplyCompletedItem(notification);
                break;

            default:
                var fallbackKind = ReadFallbackKind(notification);
                AddTimeline(fallbackKind, notification.Method, ReadItemDetail(notification.Params), notification);
                ProjectSupportedProgress(notification);
                if (fallbackKind == CodexTimelineItemKind.Error)
                {
                    ProjectStandaloneError(notification, ReadItemDetail(notification.Params));
                }
                break;
            }
        }
    }

    private void ApplyCompletedItem(AppServerNotification notification)
    {
        var kind = ReadItemCompletedKind(notification.Params);
        var detail = ReadItemDetail(notification.Params);
        if (kind == CodexTimelineItemKind.AgentMessage)
        {
            ApplyCompletedAgentMessage(notification);
        }

        AddTimeline(kind, "Item completed", detail, notification);
        ProjectItemActivity(notification, completed: true);
    }

    private static CodexTimelineItemKind ReadItemStartedKind(JsonObject parameters)
    {
        return ReadString(parameters, "item.type") switch
        {
            "command" or "command_execution" or "exec" => CodexTimelineItemKind.CommandStarted,
            "commandExecution" => CodexTimelineItemKind.CommandStarted,
            "file_change" or "file" or "patch" or "fileChange" => CodexTimelineItemKind.FileChange,
            "tool" or "tool_call" => CodexTimelineItemKind.ToolProgress,
            "mcpToolCall" or "dynamicToolCall" => CodexTimelineItemKind.ToolCall,
            "collabAgentToolCall" => CodexTimelineItemKind.Collaboration,
            "webSearch" => CodexTimelineItemKind.WebSearch,
            "plan" => CodexTimelineItemKind.PlanUpdate,
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
            "mcpToolCall" or "dynamicToolCall" => CodexTimelineItemKind.ToolCall,
            "collabAgentToolCall" => CodexTimelineItemKind.Collaboration,
            "webSearch" => CodexTimelineItemKind.WebSearch,
            "plan" => CodexTimelineItemKind.PlanUpdate,
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

    private void ApplyAgentMessageDelta(AppServerNotification notification)
    {
        var delta = ReadString(notification.Params, "delta") ?? ReadString(notification.Params, "text") ?? string.Empty;
        AddTimeline(CodexTimelineItemKind.AgentMessageDelta, "Agent message", delta, notification);
        if (string.IsNullOrEmpty(delta) || GetOrCreateAgentMessage(notification) is not { } state)
        {
            return;
        }

        state.Phase = ReadMessagePhase(notification.Params) ?? state.Phase;
        state.Text.Append(delta);
        UpdateAgentMessagePresentation(state);
    }

    private void RememberAgentMessagePhase(AppServerNotification notification)
    {
        var itemType = ReadString(notification.Params, "item.type");
        if (itemType is not ("agentMessage" or "agent_message" or "message"))
        {
            return;
        }

        if (GetOrCreateAgentMessage(notification) is { } state)
        {
            state.Phase = ReadMessagePhase(notification.Params) ?? state.Phase;
        }
    }

    private void ApplyCompletedAgentMessage(AppServerNotification notification)
    {
        if (GetOrCreateAgentMessage(notification) is not { } state)
        {
            return;
        }

        state.Phase = ReadMessagePhase(notification.Params) ?? state.Phase;
        var authoritativeText = ReadString(notification.Params, "item.text") ??
            ReadString(notification.Params, "item.message") ??
            ReadString(notification.Params, "item.content");
        if (!string.IsNullOrWhiteSpace(authoritativeText))
        {
            state.Text.Clear();
            state.Text.Append(authoritativeText);
        }
        UpdateAgentMessagePresentation(state);
    }

    private AgentMessageState? GetOrCreateAgentMessage(AppServerNotification notification)
    {
        var turn = GetNotificationTurn(notification);
        if (turn is null)
        {
            return null;
        }

        var turnKey = !string.IsNullOrWhiteSpace(turn.TurnId)
            ? turn.TurnId
            : ReadString(notification.Params, "turnId") ?? "pending";
        var itemId = ReadItemId(notification.Params) ?? "legacy-agent-message";
        var key = $"{turnKey}\u001f{itemId}";
        if (agentMessages.TryGetValue(key, out var existing))
        {
            return existing;
        }

        responsePrefixes.TryAdd(turn, turn.AssistantResponse);
        var created = new AgentMessageState(turn, itemId, nextAgentMessageSequence++);
        agentMessages[key] = created;
        return created;
    }

    private void UpdateAgentMessagePresentation(AgentMessageState state)
    {
        var activityKey = $"commentary:{state.ItemId}";
        if (string.Equals(state.Phase, "commentary", StringComparison.Ordinal))
        {
            var commentary = UnicodeTextNormalizer.RepairLegacyMojibake(state.Text.ToString());
            UpsertActivity(
                state.Turn,
                new CodexTimelineItem(
                    CodexTimelineItemKind.AssistantCommentary,
                    "Assistant update",
                    LimitDetail(commentary),
                    "item/agentMessage",
                    DateTimeOffset.Now)
                {
                    ItemId = state.ItemId,
                    ActivityKey = activityKey
                });
        }
        else
        {
            RemoveActivity(state.Turn, activityKey);
        }

        RebuildAssistantResponse(state.Turn);
    }

    private void RebuildAssistantResponse(CodexConversationTurn turn)
    {
        var response = new StringBuilder(responsePrefixes.GetValueOrDefault(turn, string.Empty));
        foreach (var state in agentMessages.Values
                     .Where(item => ReferenceEquals(item.Turn, turn) &&
                                    !string.Equals(item.Phase, "commentary", StringComparison.Ordinal))
                     .OrderBy(item => item.Sequence))
        {
            response.Append(UnicodeTextNormalizer.RepairLegacyMojibake(state.Text.ToString()));
        }

        turn.AssistantResponse = response.ToString();
        if (ReferenceEquals(ConversationTurns.LastOrDefault(), turn) || ReferenceEquals(ActiveConversationTurn, turn))
        {
            finalResponseBuilder.Clear();
            finalResponseBuilder.Append(turn.AssistantResponse);
            FinalResponse = turn.AssistantResponse;
        }
    }

    private void ReleaseAgentMessageState(CodexConversationTurn turn)
    {
        foreach (var key in agentMessages
                     .Where(pair => ReferenceEquals(pair.Value.Turn, turn))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            agentMessages.Remove(key);
        }
        responsePrefixes.Remove(turn);
    }

    private static string? ReadMessagePhase(JsonObject parameters)
    {
        var phase = ReadString(parameters, "item.phase") ?? ReadString(parameters, "phase");
        return phase is "commentary" or "final_answer" ? phase : null;
    }

    private void ProjectItemActivity(AppServerNotification notification, bool completed)
    {
        var type = ReadString(notification.Params, "item.type");
        if (type is null || GetNotificationTurn(notification) is not { } turn)
        {
            return;
        }

        var itemId = ReadItemId(notification.Params) ?? $"legacy:{type}:{ReadItemDetail(notification.Params)}";
        CodexTimelineItem? activity = type switch
        {
            "command" or "command_execution" or "commandExecution" or "exec" =>
                CreateCommandActivity(notification.Params, itemId, completed),
            "file_change" or "fileChange" or "file" or "patch" =>
                CreateFileActivity(notification.Params, itemId, completed),
            "tool" or "tool_call" or "mcpToolCall" or "dynamicToolCall" =>
                CreateToolActivity(notification.Params, itemId, completed),
            "collabAgentToolCall" => CreateCollaborationActivity(notification.Params, itemId, completed),
            "webSearch" => CreateWebSearchActivity(notification.Params, itemId, completed),
            "plan" => CreatePlanActivity(notification.Params, itemId, completed),
            _ => null
        };

        if (activity is not null)
        {
            UpsertActivity(turn, activity);
        }
    }

    private static CodexTimelineItem CreateCommandActivity(JsonObject parameters, string itemId, bool completed)
    {
        var command = ReadString(parameters, "item.command") ?? "Command";
        var status = ReadString(parameters, "item.status");
        var exitCode = ReadInt(parameters, "item.exitCode");
        var failed = IsFailureStatus(status) || exitCode is not (null or 0);
        var title = !completed ? "Running command" : failed ? "Command failed" : "Ran command";
        var suffix = completed && exitCode is not null ? $" (exit {exitCode})" : string.Empty;
        return CreateActivity(
            completed ? CodexTimelineItemKind.CommandCompleted : CodexTimelineItemKind.CommandStarted,
            title,
            $"{LimitDetail(command)}{suffix}",
            "item/commandExecution",
            itemId,
            $"command:{itemId}");
    }

    private static CodexTimelineItem CreateFileActivity(JsonObject parameters, string itemId, bool completed)
    {
        var paths = ReadChangedPaths(parameters);
        var status = ReadString(parameters, "item.status");
        var failed = IsFailureStatus(status);
        var title = !completed
            ? "Changing files"
            : failed ? "File changes failed" : paths.Count == 0 ? "Changed files" : paths.Count == 1 ? "Changed 1 file" : $"Changed {paths.Count} files";
        var detail = paths.Count == 0 ? "Workspace files" : string.Join(", ", paths.Take(4));
        if (paths.Count > 4)
        {
            detail += $" and {paths.Count - 4} more";
        }
        return CreateActivity(CodexTimelineItemKind.FileChange, title, detail, "item/fileChange", itemId, $"file:{itemId}");
    }

    private static CodexTimelineItem CreateToolActivity(JsonObject parameters, string itemId, bool completed)
    {
        var server = ReadString(parameters, "item.server") ?? ReadString(parameters, "item.namespace");
        var tool = ReadString(parameters, "item.tool") ?? ReadString(parameters, "item.name") ?? "tool";
        var label = string.IsNullOrWhiteSpace(server) ? tool : $"{server}/{tool}";
        var status = ReadString(parameters, "item.status");
        var error = ReadString(parameters, "item.error.message");
        var failed = IsFailureStatus(status) || error is not null;
        var title = !completed ? "Using tool" : failed ? "Tool failed" : "Used tool";
        var detail = string.IsNullOrWhiteSpace(error) ? label : $"{label} — {error}";
        return CreateActivity(CodexTimelineItemKind.ToolCall, title, LimitDetail(detail), "item/toolCall", itemId, $"tool:{itemId}");
    }

    private static CodexTimelineItem CreateCollaborationActivity(JsonObject parameters, string itemId, bool completed)
    {
        var tool = ReadString(parameters, "item.tool") ?? "agent task";
        var prompt = ReadString(parameters, "item.prompt");
        var status = ReadString(parameters, "item.status");
        var title = !completed ? "Delegating work" : IsFailureStatus(status) ? "Delegated work failed" : "Delegated work";
        var detail = string.IsNullOrWhiteSpace(prompt) ? tool : $"{tool}: {prompt}";
        return CreateActivity(CodexTimelineItemKind.Collaboration, title, LimitDetail(detail), "item/collaboration", itemId, $"collaboration:{itemId}");
    }

    private static CodexTimelineItem CreateWebSearchActivity(JsonObject parameters, string itemId, bool completed)
    {
        var query = ReadString(parameters, "item.query") ?? "Web search";
        return CreateActivity(
            CodexTimelineItemKind.WebSearch,
            completed ? "Searched the web" : "Searching the web",
            LimitDetail(query),
            "item/webSearch",
            itemId,
            $"search:{itemId}");
    }

    private static CodexTimelineItem CreatePlanActivity(JsonObject parameters, string itemId, bool completed)
    {
        var text = ReadString(parameters, "item.text") ?? "Plan updated";
        return CreateActivity(
            CodexTimelineItemKind.PlanUpdate,
            completed ? "Updated plan" : "Updating plan",
            LimitDetail(text),
            "item/plan",
            itemId,
            "plan:turn");
    }

    private void ProjectSupportedProgress(AppServerNotification notification)
    {
        if (string.Equals(notification.Method, "turn/plan/updated", StringComparison.Ordinal))
        {
            ProjectTurnPlanUpdate(notification);
            return;
        }

        if (!string.Equals(notification.Method, "item/mcpToolCall/progress", StringComparison.Ordinal) ||
            GetNotificationTurn(notification) is not { } turn ||
            ReadItemId(notification.Params) is not { } itemId)
        {
            return;
        }

        var index = FindActivityIndex(turn, $"tool:{itemId}");
        var progress = ReadString(notification.Params, "message") ?? ReadString(notification.Params, "status");
        if (index < 0 || string.IsNullOrWhiteSpace(progress))
        {
            return;
        }

        var existing = turn.Activity[index];
        var baseDetail = existing.Detail.Split(" — ", 2, StringSplitOptions.None)[0];
        turn.Activity[index] = existing with { Detail = LimitDetail($"{baseDetail} — {progress}") };
    }

    private void ProjectTurnPlanUpdate(AppServerNotification notification)
    {
        if (GetNotificationTurn(notification) is not { } turn)
        {
            return;
        }

        var explanation = ReadString(notification.Params, "explanation");
        var steps = notification.Params["plan"] is JsonArray plan
            ? plan.OfType<JsonObject>()
                .Select(step => ReadString(step, "step"))
                .Where(step => !string.IsNullOrWhiteSpace(step))
                .Cast<string>()
                .ToList()
            : [];
        var detail = !string.IsNullOrWhiteSpace(explanation)
            ? explanation
            : steps.Count == 0 ? "Plan updated" : string.Join("; ", steps);
        UpsertActivity(
            turn,
            CreateActivity(
                CodexTimelineItemKind.PlanUpdate,
                "Updated plan",
                LimitDetail(detail),
                notification.Method,
                "turn-plan",
                "plan:turn"));
    }

    private void ProjectStandaloneError(AppServerNotification notification, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || GetNotificationTurn(notification) is not { } turn)
        {
            return;
        }

        var itemId = ReadItemId(notification.Params) ?? notification.Method;
        UpsertActivity(
            turn,
            CreateActivity(
                CodexTimelineItemKind.Error,
                "Action needed",
                LimitDetail(detail),
                notification.Method,
                itemId,
                $"error:{itemId}"));
    }

    private static CodexTimelineItem CreateActivity(
        CodexTimelineItemKind kind,
        string title,
        string detail,
        string method,
        string itemId,
        string activityKey) => new(kind, title, detail, method, DateTimeOffset.Now)
        {
            ItemId = itemId,
            ActivityKey = activityKey
        };

    private static IReadOnlyList<string> ReadChangedPaths(JsonObject parameters)
    {
        if (parameters["item"]?["changes"] is not JsonArray changes)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var change in changes.OfType<JsonObject>())
        {
            var path = ReadString(change, "path") ??
                ReadString(change, "movedPath") ??
                ReadString(change, "movePath");
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    private static bool IsFailureStatus(string? status) =>
        status?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true ||
        status?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ||
        status?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true ||
        status?.Contains("declin", StringComparison.OrdinalIgnoreCase) == true;

    private static string LimitDetail(string? detail, int maximumLength = 600)
    {
        var value = detail?.Trim() ?? string.Empty;
        return value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";
    }

    private static string? ReadItemId(JsonObject parameters) =>
        ReadString(parameters, "item.id") ?? ReadString(parameters, "itemId");

    private static void UpsertActivity(CodexConversationTurn turn, CodexTimelineItem item)
    {
        var index = FindActivityIndex(turn, item.ActivityKey);
        if (index >= 0)
        {
            turn.Activity[index] = item;
            return;
        }
        AddBounded(turn.Activity, item, MaximumTimelineItems);
    }

    private static void RemoveActivity(CodexConversationTurn turn, string activityKey)
    {
        var index = FindActivityIndex(turn, activityKey);
        if (index >= 0)
        {
            turn.Activity.RemoveAt(index);
        }
    }

    private static int FindActivityIndex(CodexConversationTurn turn, string activityKey)
    {
        for (var index = 0; index < turn.Activity.Count; index++)
        {
            if (string.Equals(turn.Activity[index].ActivityKey, activityKey, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
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

    private void AddTimeline(CodexTimelineItemKind kind, string title, string? detail, AppServerNotification notification)
    {
        var item = new CodexTimelineItem(
            kind,
            title,
            detail ?? string.Empty,
            notification.Method,
            DateTimeOffset.Now)
        {
            ItemId = ReadItemId(notification.Params) ?? string.Empty
        };
        AddBounded(TimelineItems, item, MaximumTimelineItems);
    }

    private static void SanitizeRestoredActivity(CodexConversationTurn turn)
    {
        var sanitized = new List<CodexTimelineItem>();
        foreach (var item in turn.Activity.Where(IsUserFacingLegacyActivity))
        {
            var dedupeKey = !string.IsNullOrWhiteSpace(item.ActivityKey)
                ? item.ActivityKey
                : item.Kind is CodexTimelineItemKind.CommandStarted or CodexTimelineItemKind.CommandCompleted
                    ? $"legacy-command:{item.Detail}"
                    : string.Empty;
            if (string.IsNullOrWhiteSpace(dedupeKey))
            {
                sanitized.Add(item);
                continue;
            }

            var existingIndex = sanitized.FindIndex(existing =>
                string.Equals(existing.ActivityKey, dedupeKey, StringComparison.Ordinal) ||
                (dedupeKey.StartsWith("legacy-command:", StringComparison.Ordinal) &&
                 existing.Kind is CodexTimelineItemKind.CommandStarted or CodexTimelineItemKind.CommandCompleted &&
                 string.Equals(existing.Detail, item.Detail, StringComparison.Ordinal)));
            var normalized = string.IsNullOrWhiteSpace(item.ActivityKey)
                ? item with { ActivityKey = dedupeKey }
                : item;
            if (existingIndex >= 0)
            {
                if (item.Kind == CodexTimelineItemKind.CommandCompleted ||
                    sanitized[existingIndex].Kind == CodexTimelineItemKind.CommandStarted)
                {
                    sanitized[existingIndex] = normalized;
                }
            }
            else
            {
                sanitized.Add(normalized);
            }
        }

        turn.Activity.Clear();
        foreach (var item in sanitized.TakeLast(MaximumTimelineItems))
        {
            turn.Activity.Add(item);
        }
    }

    private static bool IsUserFacingLegacyActivity(CodexTimelineItem item) => item.Kind switch
    {
        CodexTimelineItemKind.CommandStarted or
        CodexTimelineItemKind.CommandCompleted or
        CodexTimelineItemKind.FileChange or
        CodexTimelineItemKind.Error or
        CodexTimelineItemKind.UserGuidance or
        CodexTimelineItemKind.AssistantCommentary or
        CodexTimelineItemKind.ToolCall or
        CodexTimelineItemKind.WebSearch or
        CodexTimelineItemKind.PlanUpdate or
        CodexTimelineItemKind.Collaboration => true,
        CodexTimelineItemKind.ToolProgress => item.Method.StartsWith("item/", StringComparison.Ordinal),
        _ => false
    };

    private static void AddBounded<T>(ObservableCollection<T> collection, T item, int maximumCount)
    {
        while (collection.Count >= maximumCount)
        {
            collection.RemoveAt(0);
        }

        collection.Add(item);
    }

    private sealed class AgentMessageState(
        CodexConversationTurn turn,
        string itemId,
        long sequence)
    {
        public CodexConversationTurn Turn { get; } = turn;
        public string ItemId { get; } = itemId;
        public long Sequence { get; } = sequence;
        public string? Phase { get; set; }
        public StringBuilder Text { get; } = new();
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
    DateTimeOffset Timestamp)
{
    public string ItemId { get; init; } = string.Empty;

    public string ActivityKey { get; init; } = string.Empty;

    [JsonIgnore]
    public string CategoryLabel => Kind switch
    {
        CodexTimelineItemKind.CommandStarted or CodexTimelineItemKind.CommandCompleted => "Command",
        CodexTimelineItemKind.FileChange => "Files",
        CodexTimelineItemKind.AssistantCommentary => "Update",
        CodexTimelineItemKind.ToolCall or CodexTimelineItemKind.ToolProgress => "Tool",
        CodexTimelineItemKind.WebSearch => "Search",
        CodexTimelineItemKind.PlanUpdate => "Plan",
        CodexTimelineItemKind.Collaboration => "Agent",
        CodexTimelineItemKind.Error => "Error",
        CodexTimelineItemKind.UserGuidance => "Guidance",
        _ => "Details"
    };
}

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
    UserGuidance,
    AssistantCommentary,
    ToolCall,
    WebSearch,
    PlanUpdate,
    Collaboration
}

public enum CodexTurnStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled
}
