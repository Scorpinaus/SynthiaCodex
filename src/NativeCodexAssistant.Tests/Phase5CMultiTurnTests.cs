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
        ("task composer distinguishes first turn and follow-up", ComposerLabelsFollowUpAsync)
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
        Assert(service.ConversationTurns[1].Activity.Count == 2, "turn lifecycle activity is grouped with the second turn");
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
            {"id":1,"result":{"thread":{"id":"thread-1","turns":[{"id":"turn-1","status":"completed","startedAt":100,"completedAt":101,"items":[{"id":"u1","type":"userMessage","content":[{"type":"text","text":"Question"}]},{"id":"a1","type":"agentMessage","text":"Answer"}]}]}}}
            """);

        var result = await read;
        Assert(result.ThreadId == "thread-1", "thread id parsed");
        Assert(result.Turns.Count == 1, "one canonical turn parsed");
        Assert(result.Turns[0].UserPrompt == "Question", "canonical prompt parsed");
        Assert(result.Turns[0].AssistantResponse == "Answer", "canonical response parsed");
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
