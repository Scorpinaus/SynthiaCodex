using System.Text.Json.Nodes;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure.Codex;

internal static class Phase5CMultiTurnTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("multi-turn reducer keeps sequential turns independent", ReducerKeepsTurnsIndependentAsync),
        ("thread read parses canonical conversation history", ThreadReadParsesHistoryAsync),
        ("conversation persistence remains backward compatible", ConversationPersistenceIsCompatibleAsync),
        ("task composer distinguishes first turn and follow-up", ComposerLabelsFollowUpAsync),
        ("turn activity presentation follows content and status", ActivityPresentationFollowsTurnStateAsync),
        ("turn activity suppresses protocol noise", ActivitySuppressesProtocolNoiseAsync),
        ("command and tool activity updates stable rows", ActivityUpdatesStableRowsAsync),
        ("interleaved activities retain independent identity", InterleavedActivitiesRetainIdentityAsync),
        ("assistant commentary stays separate from final response", CommentaryStaysSeparateFromFinalResponseAsync),
        ("Unicode repair is conservative and idempotent", UnicodeRepairIsConservativeAsync),
        ("streamed and restored mojibake is repaired for presentation", StreamedAndRestoredMojibakeIsRepairedAsync),
        ("supported work items project friendly activity", SupportedWorkItemsProjectFriendlyActivityAsync),
        ("restored activity removes legacy protocol noise", RestoredActivityRemovesLegacyNoiseAsync)
    ];

    private static Task ReducerKeepsTurnsIndependentAsync()
    {
        var service = new CodexThreadService();
        service.Restore("thread-1", null, null, null);

        service.BeginTurn("First question");
        service.BindPendingTurn("turn-1");
        service.ApplyNotification(Notification("item/agentMessage/delta", "turn-1", delta: "First answer"));
        service.ApplyNotification(Notification("turn/completed", "turn-1", status: "completed"));

        service.BeginTurn("Follow-up question");
        Assert(service.FinalResponse == string.Empty, "new turn starts with an empty compatibility response");
        service.BindPendingTurn("turn-2");
        service.ApplyNotification(Notification("item/started", "turn-2"));
        service.ApplyNotification(Notification("item/agentMessage/delta", "turn-2", delta: "Second answer"));
        service.ApplyNotification(Notification("turn/completed", "turn-2", status: "completed"));

        Assert(service.ConversationTurns.Count == 2, "two turns retained");
        Assert(service.ConversationTurns[0].UserPrompt == "First question", "first prompt retained");
        Assert(service.ConversationTurns[0].AssistantResponse == "First answer", "first response retained");
        Assert(service.ConversationTurns[1].UserPrompt == "Follow-up question", "follow-up retained");
        Assert(service.ConversationTurns[1].AssistantResponse == "Second answer", "second response is independent");
        Assert(service.ConversationTurns[1].Activity.Count == 1, "only the command is shown as second-turn activity");
        Assert(service.ConversationTurns[1].Activity[0].CategoryLabel == "Command", "command has a friendly category");
        return Task.CompletedTask;
    }

    private static async Task ThreadReadParsesHistoryAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("phase_5c", "Phase 5C", "1.0"));

        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await initialize;

        var read = client.ReadThreadAsync(new CodexThreadReadRequest("thread-1"));
        await transport.WaitForClientMessageCountAsync(3);
        var request = JsonNode.Parse(transport.ClientMessages[2])!.AsObject();
        Assert(request["method"]?.GetValue<string>() == "thread/read", "thread/read method sent");
        Assert(request["params"]?["includeTurns"]?.GetValue<bool>() == true, "turn history requested");
        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"thread-1","turns":[{"id":"turn-1","status":"completed","startedAt":100,"completedAt":101,"items":[{"id":"u1","type":"userMessage","content":[{"type":"text","text":"Question"}]},{"id":"a1","type":"agentMessage","text":"I\u00e2\u20ac\u2122m ready"}]}]}}}
            """);

        var result = await read;
        Assert(result.ThreadId == "thread-1", "thread id parsed");
        Assert(result.Turns.Count == 1, "one canonical turn parsed");
        Assert(result.Turns[0].UserPrompt == "Question", "canonical prompt parsed");
        Assert(result.Turns[0].AssistantResponse == "I\u2019m ready", "canonical response is repaired while parsed");
    }

    private static Task ConversationPersistenceIsCompatibleAsync()
    {
        var legacy = new ProjectThreadState
        {
            ProjectPath = Path.GetTempPath(),
            ThreadId = "legacy",
            Preview = "Legacy prompt",
            FinalResponse = "Legacy response"
        };
        var workspace = new CodexThreadWorkspace();
        var restored = workspace.Restore(legacy);
        Assert(restored.ConversationTurns.Count == 1, "legacy state becomes one visible turn");
        Assert(restored.ConversationTurns[0].UserPrompt == "Legacy prompt", "legacy prompt retained");

        legacy.ConversationTurns = [.. restored.SnapshotConversation()];
        var settings = new AppSettings();
        new ThreadStore().Upsert(settings, legacy);
        var snapshot = AppSettingsSnapshot.Create(settings);
        legacy.ConversationTurns[0].UserPrompt = "Changed";
        Assert(snapshot.ProjectThreads[0].ConversationTurns[0].UserPrompt == "Legacy prompt", "settings snapshot deep-copies turns");

        var bounded = new CodexThreadService();
        bounded.Restore("bounded", null, null, null);
        for (var turnIndex = 0; turnIndex < 105; turnIndex++)
        {
            var turn = bounded.BeginTurn($"Prompt {turnIndex}");
            bounded.BindPendingTurn($"turn-{turnIndex}");
            for (var itemIndex = 0; itemIndex < 105; itemIndex++)
            {
                turn.Activity.Add(new CodexTimelineItem(
                    CodexTimelineItemKind.ToolProgress,
                    "Activity",
                    itemIndex.ToString(),
                    "test",
                    DateTimeOffset.UtcNow));
            }
            turn.Status = CodexTurnStatus.Completed;
        }
        var boundedSnapshot = bounded.SnapshotConversation();
        Assert(boundedSnapshot.Count == CodexThreadService.MaximumConversationTurns, "persisted turn history is bounded");
        Assert(boundedSnapshot.All(turn => turn.Activity.Count == CodexThreadService.MaximumPersistedActivityItemsPerTurn), "persisted per-turn activity is bounded");
        return Task.CompletedTask;
    }

    private static Task ComposerLabelsFollowUpAsync()
    {
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false);
        Assert(viewModel.ComposerActionLabel == "Run task", "first-turn label");
        viewModel.ThreadService.BeginTurn("Question");
        viewModel.NotifyResponseChanged();
        Assert(viewModel.ComposerActionLabel == "Send follow-up", "follow-up label");
        return Task.CompletedTask;
    }

    private static Task ActivityPresentationFollowsTurnStateAsync()
    {
        var turn = new CodexConversationTurn();
        Assert(!turn.HasActivity, "new turn has no visible activity region");
        Assert(turn.ActivitySummary == "0 activity items", "empty activity summary");

        turn.Status = CodexTurnStatus.Running;
        Assert(turn.IsActivityExpanded, "running turn expands activity");
        turn.Activity.Add(new CodexTimelineItem(
            CodexTimelineItemKind.ToolProgress,
            "Inspecting files",
            "Reading the transcript view",
            "test",
            DateTimeOffset.UtcNow));
        Assert(turn.HasActivity, "activity region becomes visible when an item arrives");
        Assert(turn.ActivitySummary == "1 activity item", "single activity summary");

        turn.Status = CodexTurnStatus.Completed;
        Assert(!turn.IsActivityExpanded, "completed turn collapses historical activity");
        Assert(turn.HasActivity, "completed turn retains its activity region");
        return Task.CompletedTask;
    }

    private static Task ActivitySuppressesProtocolNoiseAsync()
    {
        var service = CreateRunningService("turn-noise");
        service.ApplyNotification(NotificationWithItem("item/started", "turn-noise", "reason-1", "reasoning"));
        service.ApplyNotification(new AppServerNotification(
            "item/reasoning/summaryTextDelta",
            Params("turn-noise", "reason-1", message: "private detail")));
        service.ApplyNotification(new AppServerNotification(
            "item/commandExecution/outputDelta",
            Params("turn-noise", "command-1", message: "many output bytes")));
        service.ApplyNotification(new AppServerNotification(
            "thread/tokenUsage/updated",
            Params("turn-noise", null, message: "tokens")));
        service.ApplyNotification(Notification("turn/completed", "turn-noise", status: "completed"));

        Assert(service.ActiveConversationTurn!.Activity.Count == 0, "reasoning, output, token, and lifecycle notifications stay hidden");
        Assert(service.RawEvents.Count == 5, "all suppressed notifications remain available as raw diagnostics");
        Assert(service.TimelineItems.Count == 5, "all suppressed notifications remain available in the diagnostic timeline");
        return Task.CompletedTask;
    }

    private static Task ActivityUpdatesStableRowsAsync()
    {
        var service = CreateRunningService("turn-upsert");
        service.ApplyNotification(NotificationWithItem(
            "item/started",
            "turn-upsert",
            "command-1",
            "commandExecution",
            item =>
            {
                item["command"] = "dotnet test";
                item["status"] = "inProgress";
            }));
        service.ApplyNotification(new AppServerNotification(
            "item/commandExecution/outputDelta",
            Params("turn-upsert", "command-1", message: "PASS one")));
        service.ApplyNotification(NotificationWithItem(
            "item/completed",
            "turn-upsert",
            "command-1",
            "commandExecution",
            item =>
            {
                item["command"] = "dotnet test";
                item["status"] = "completed";
                item["exitCode"] = 0;
            }));

        service.ApplyNotification(NotificationWithItem(
            "item/started",
            "turn-upsert",
            "tool-1",
            "mcpToolCall",
            item =>
            {
                item["server"] = "docs";
                item["tool"] = "search";
                item["status"] = "inProgress";
            }));
        service.ApplyNotification(new AppServerNotification(
            "item/mcpToolCall/progress",
            Params("turn-upsert", "tool-1", message: "Reading results")));
        service.ApplyNotification(NotificationWithItem(
            "item/completed",
            "turn-upsert",
            "tool-1",
            "mcpToolCall",
            item =>
            {
                item["server"] = "docs";
                item["tool"] = "search";
                item["status"] = "completed";
            }));

        var activity = service.ActiveConversationTurn!.Activity;
        Assert(activity.Count == 2, "command and tool lifecycles produce two rows total");
        Assert(activity[0].Title == "Ran command" && activity[0].Detail.Contains("dotnet test", StringComparison.Ordinal), "command row is updated to completion");
        Assert(activity[0].ItemId == "command-1", "command retains stable item identity");
        Assert(activity[1].Title == "Used tool" && activity[1].Detail == "docs/search", "tool progress is consolidated into the completed tool row");
        return Task.CompletedTask;
    }

    private static Task CommentaryStaysSeparateFromFinalResponseAsync()
    {
        var service = CreateRunningService("turn-message");
        service.ApplyNotification(NotificationWithItem(
            "item/started", "turn-message", "message-1", "agentMessage"));
        service.ApplyNotification(new AppServerNotification(
            "item/agentMessage/delta",
            Params("turn-message", "message-1", delta: "I am checking the tests.")));
        Assert(service.ActiveConversationTurn!.AssistantResponse == "I am checking the tests.", "unknown streaming phase uses the compatibility response until classified");
        service.ApplyNotification(NotificationWithItem(
            "item/completed",
            "turn-message",
            "message-1",
            "agentMessage",
            item =>
            {
                item["phase"] = "commentary";
                item["text"] = "I am checking the tests.";
            }));

        service.ApplyNotification(NotificationWithItem(
            "item/started", "turn-message", "message-2", "agentMessage", item => item["phase"] = "final_answer"));
        service.ApplyNotification(new AppServerNotification(
            "item/agentMessage/delta",
            Params("turn-message", "message-2", delta: "All tests passed.")));
        service.ApplyNotification(NotificationWithItem(
            "item/completed",
            "turn-message",
            "message-2",
            "agentMessage",
            item =>
            {
                item["phase"] = "final_answer";
                item["text"] = "All tests passed.";
            }));

        var turn = service.ActiveConversationTurn!;
        Assert(turn.Activity.Count == 1, "commentary produces one visible update");
        Assert(turn.Activity[0].Kind == CodexTimelineItemKind.AssistantCommentary, "commentary uses the update category");
        Assert(turn.Activity[0].Detail == "I am checking the tests.", "completed commentary is authoritative");
        Assert(turn.AssistantResponse == "All tests passed.", "only final answer text reaches the response");

        var legacy = CreateRunningService("turn-legacy-message");
        legacy.ApplyNotification(new AppServerNotification(
            "item/agentMessage/delta",
            Params("turn-legacy-message", "legacy-message", delta: "Legacy final text")));
        Assert(legacy.ActiveConversationTurn!.AssistantResponse == "Legacy final text", "unknown message phase preserves streaming compatibility");
        return Task.CompletedTask;
    }

    private static Task InterleavedActivitiesRetainIdentityAsync()
    {
        var service = CreateRunningService("turn-interleaved");
        service.ApplyNotification(NotificationWithItem(
            "item/started", "turn-interleaved", "command-a", "commandExecution", item => item["command"] = "dotnet build"));
        service.ApplyNotification(NotificationWithItem(
            "item/started", "turn-interleaved", "command-b", "commandExecution", item => item["command"] = "dotnet test"));
        service.ApplyNotification(NotificationWithItem(
            "item/completed", "turn-interleaved", "command-b", "commandExecution", item =>
            {
                item["command"] = "dotnet test";
                item["status"] = "completed";
                item["exitCode"] = 0;
            }));
        service.ApplyNotification(NotificationWithItem(
            "item/completed", "turn-interleaved", "command-a", "commandExecution", item =>
            {
                item["command"] = "dotnet build";
                item["status"] = "failed";
                item["exitCode"] = 1;
            }));

        var activity = service.ActiveConversationTurn!.Activity;
        Assert(activity.Count == 2, "interleaved commands keep two rows");
        Assert(activity[0].ItemId == "command-a" && activity[0].Title == "Command failed", "first command is updated by its own completion");
        Assert(activity[1].ItemId == "command-b" && activity[1].Title == "Ran command", "second command is updated by its own completion");
        return Task.CompletedTask;
    }

    private static Task UnicodeRepairIsConservativeAsync()
    {
        const string corrupted = "I\u00E2\u20AC\u2122m ready\u00E2\u20AC\u201Dnow\u00E2\u20AC\u00A6";
        const string expected = "I\u2019m ready\u2014now\u2026";
        var repaired = UnicodeTextNormalizer.RepairLegacyMojibake(corrupted);

        Assert(repaired == expected, "smart punctuation is recovered exactly");
        Assert(UnicodeTextNormalizer.RepairLegacyMojibake(repaired) == expected, "repair is idempotent");

        const string legitimate = "S\u00E3o Tom\u00E9 \u2014 caf\u00E9, \u0395\u03BB\u03BB\u03B7\u03BD\u03B9\u03BA\u03AC \u65E5\u672C\u8A9E \uD83D\uDE80";
        Assert(UnicodeTextNormalizer.RepairLegacyMojibake(legitimate) == legitimate, "legitimate multilingual Unicode is unchanged");
        Assert(
            UnicodeTextNormalizer.RepairLegacyMojibake("\u65E5\u672C\u8A9E " + corrupted) == "\u65E5\u672C\u8A9E " + expected,
            "a corrupted span is repaired without changing adjacent non-Latin text");
        Assert(UnicodeTextNormalizer.RepairLegacyMojibake("The letter \u00E2 is valid here") == "The letter \u00E2 is valid here", "an isolated valid character is unchanged");
        return Task.CompletedTask;
    }

    private static Task StreamedAndRestoredMojibakeIsRepairedAsync()
    {
        const string corrupted = "I\u00E2\u20AC\u2122m ready\u00E2\u20AC\u201Dnow";
        const string expected = "I\u2019m ready\u2014now";
        var service = CreateRunningService("turn-unicode");
        service.ApplyNotification(new AppServerNotification(
            "item/agentMessage/delta",
            Params("turn-unicode", "message-unicode", delta: "I\u00E2")));
        service.ApplyNotification(new AppServerNotification(
            "item/agentMessage/delta",
            Params("turn-unicode", "message-unicode", delta: "\u20AC")));
        service.ApplyNotification(new AppServerNotification(
            "item/agentMessage/delta",
            Params("turn-unicode", "message-unicode", delta: "\u2122m ready\u00E2\u20AC\u201Dnow")));
        Assert(service.ActiveConversationTurn!.AssistantResponse == expected, "repair handles a mojibake sequence split across deltas");

        var rawEvent = $"item/completed: {corrupted}";
        var restored = new CodexThreadService();
        restored.Restore(
            "thread-restored-unicode",
            corrupted,
            null,
            [rawEvent],
            conversationTurns:
            [
                new CodexConversationTurnSnapshot
                {
                    TurnId = "turn-restored-unicode",
                    AssistantResponse = corrupted,
                    Status = CodexTurnStatus.Completed,
                    Activity =
                    [
                        new CodexTimelineItem(
                            CodexTimelineItemKind.AssistantCommentary,
                            "Assistant update",
                            corrupted,
                            "item/agentMessage",
                            DateTimeOffset.UtcNow)
                    ]
                }
            ]);

        Assert(restored.FinalResponse == expected, "legacy final response is repaired");
        Assert(restored.ConversationTurns[0].AssistantResponse == expected, "restored assistant response is repaired");
        Assert(restored.ConversationTurns[0].Activity[0].Detail == expected, "restored user-facing activity is repaired");
        Assert(restored.RawEvents[0] == rawEvent, "raw protocol diagnostics remain unchanged");
        return Task.CompletedTask;
    }

    private static Task SupportedWorkItemsProjectFriendlyActivityAsync()
    {
        var service = CreateRunningService("turn-work");
        service.ApplyNotification(NotificationWithItem(
            "item/completed", "turn-work", "files-1", "fileChange", item =>
            {
                item["status"] = "completed";
                item["changes"] = new JsonArray(
                    new JsonObject { ["path"] = "src/App.xaml" },
                    new JsonObject { ["path"] = "src/View.xaml" });
            }));
        service.ApplyNotification(NotificationWithItem(
            "item/completed", "turn-work", "search-1", "webSearch", item => item["query"] = "Codex app-server items"));
        service.ApplyNotification(NotificationWithItem(
            "item/completed", "turn-work", "plan-1", "plan", item => item["text"] = "Inspect, implement, verify"));
        var planUpdate = Params("turn-work", null);
        planUpdate["explanation"] = "Implementation is complete; verification remains.";
        planUpdate["plan"] = new JsonArray(
            new JsonObject { ["step"] = "Implement", ["status"] = "completed" },
            new JsonObject { ["step"] = "Verify", ["status"] = "inProgress" });
        service.ApplyNotification(new AppServerNotification("turn/plan/updated", planUpdate));
        service.ApplyNotification(NotificationWithItem(
            "item/completed", "turn-work", "agent-1", "collabAgentToolCall", item =>
            {
                item["tool"] = "spawn_agent";
                item["prompt"] = "Review the reducer";
                item["status"] = "completed";
            }));

        var activity = service.ActiveConversationTurn!.Activity;
        Assert(activity.Count == 4, "four supported work items produce four rows");
        Assert(activity.Select(item => item.CategoryLabel).SequenceEqual(["Files", "Search", "Plan", "Agent"]), "work items expose friendly categories");
        Assert(activity[0].Title == "Changed 2 files" && activity[0].Detail.Contains("src/App.xaml", StringComparison.Ordinal), "file activity is summarized");
        Assert(activity[1].Detail == "Codex app-server items", "search query is shown");
        Assert(activity[2].Detail == "Implementation is complete; verification remains.", "stable plan update replaces the experimental plan item");
        return Task.CompletedTask;
    }

    private static Task RestoredActivityRemovesLegacyNoiseAsync()
    {
        var snapshot = new CodexConversationTurnSnapshot
        {
            TurnId = "turn-restored",
            Status = CodexTurnStatus.Completed,
            Activity =
            [
                Timeline(CodexTimelineItemKind.TurnStarted, "Turn started", "turn-restored", "turn/started"),
                Timeline(CodexTimelineItemKind.Raw, "token", "100", "thread/tokenUsage/updated"),
                Timeline(CodexTimelineItemKind.CommandStarted, "Item started", "dotnet test", "item/started"),
                Timeline(CodexTimelineItemKind.CommandCompleted, "Item completed", "dotnet test", "item/completed"),
                Timeline(CodexTimelineItemKind.Error, "Error", "Permission denied", "error")
            ]
        };
        var service = new CodexThreadService();
        service.Restore("thread-restored", null, null, null, conversationTurns: [snapshot]);

        var activity = service.ConversationTurns[0].Activity;
        Assert(activity.Count == 2, "legacy lifecycle and raw rows are removed and command duplicates collapse");
        Assert(activity[0].Kind == CodexTimelineItemKind.CommandCompleted, "completed legacy command wins over its start row");
        Assert(activity[1].Kind == CodexTimelineItemKind.Error, "actionable legacy errors remain visible");
        return Task.CompletedTask;
    }

    private static CodexThreadService CreateRunningService(string turnId)
    {
        var service = new CodexThreadService();
        service.Restore("thread-1", null, null, null);
        service.BeginTurn("Question");
        service.BindPendingTurn(turnId);
        return service;
    }

    private static AppServerNotification NotificationWithItem(
        string method,
        string turnId,
        string itemId,
        string itemType,
        Action<JsonObject>? configure = null)
    {
        var item = new JsonObject { ["id"] = itemId, ["type"] = itemType };
        configure?.Invoke(item);
        var parameters = Params(turnId, itemId);
        parameters["item"] = item;
        return new AppServerNotification(method, parameters);
    }

    private static JsonObject Params(
        string turnId,
        string? itemId,
        string? delta = null,
        string? message = null)
    {
        var parameters = new JsonObject
        {
            ["threadId"] = "thread-1",
            ["turnId"] = turnId
        };
        if (itemId is not null)
        {
            parameters["itemId"] = itemId;
        }
        if (delta is not null)
        {
            parameters["delta"] = delta;
        }
        if (message is not null)
        {
            parameters["message"] = message;
        }
        return parameters;
    }

    private static CodexTimelineItem Timeline(
        CodexTimelineItemKind kind,
        string title,
        string detail,
        string method) => new(kind, title, detail, method, DateTimeOffset.UtcNow);

    private static AppServerNotification Notification(string method, string turnId, string? delta = null, string? status = null)
    {
        var parameters = new JsonObject
        {
            ["threadId"] = "thread-1",
            ["turnId"] = turnId,
            ["turn"] = new JsonObject { ["id"] = turnId },
            ["item"] = new JsonObject { ["type"] = "commandExecution", ["command"] = "test" }
        };
        if (delta is not null)
        {
            parameters["delta"] = delta;
        }
        if (status is not null)
        {
            parameters["status"] = status;
        }
        return new AppServerNotification(method, parameters);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
