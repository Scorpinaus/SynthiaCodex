using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Codex;
using Xunit;

public sealed class TurnExecutionUseCaseServiceTests
{
    [Fact]
    public async Task Start_publishes_running_state_before_automatic_rename_finishes()
    {
        await using var transport = new FakeAppServerTransport();
        var coordinator = CreateCoordinator(transport);
        await ConnectAsync(coordinator, transport);
        var settingsStore = new FakeSettingsStore();
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var queues = new CodexFollowUpQueueWorkspace();
        var lifecycle = new ThreadLifecycleUseCaseService(
            coordinator,
            new FakeGitService(Path.GetTempPath()),
            new FakeWorktreeService(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "turn-worktree")),
            store,
            workspace,
            settingsStore);
        var persistence = new ThreadStatePersistenceUseCaseService(settingsStore, store, workspace);
        var conversations = new ConversationWorkflowController(store, workspace, queues);
        var turns = new TurnExecutionUseCaseService(coordinator, conversations, lifecycle, persistence);
        var settings = new AppSettings();
        await lifecycle.CreateAsync(new ThreadCreateRequest(
            settings,
            ThreadScopeKey.General,
            "thread-turn",
            "Thread 1",
            Path.GetTempPath(),
            null,
            new ThreadInstructionSnapshot(null, null),
            IsTitlePlaceholder: true));
        conversations.MarkLoaded("thread-turn");
        var startedPublished = new TaskCompletionSource<TurnExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = turns.StartAsync(new TurnExecutionRequest(
            settings,
            "thread-turn",
            "First real prompt",
            [],
            new CodexTurnStartRequest("thread-turn", "First real prompt", Path.GetTempPath(), Sandbox: null),
            "First real prompt",
            TurnStarted: result => startedPublished.TrySetResult(result)));
        var turnRequest = await WaitForRequestAsync(transport, "turn/start");
        transport.ServerSend($"{{\"id\":{turnRequest["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-1\"}}}}}}");

        var published = await startedPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CodexTurnStatus.Running, published.Status);
        Assert.True(conversations.IsRunning("thread-turn"));
        var renameRequest = await WaitForRequestAsync(transport, "thread/name/set");
        Assert.False(execution.IsCompleted);
        transport.ServerSend($"{{\"id\":{renameRequest["id"]!.ToJsonString()},\"result\":{{}}}}");

        var result = await execution;
        Assert.True(result.AutomaticTitleApplied);
        Assert.False(settings.ProjectThreads.Single().IsTitlePlaceholder);
        Assert.Single(result.Snapshot.ConversationTurns);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_delegates_the_exact_thread_and_turn_identity()
    {
        await using var transport = new FakeAppServerTransport();
        var coordinator = CreateCoordinator(transport);
        await ConnectAsync(coordinator, transport);
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var settingsStore = new FakeSettingsStore();
        var lifecycle = new ThreadLifecycleUseCaseService(
            coordinator,
            new FakeGitService(Path.GetTempPath()),
            new FakeWorktreeService(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "cancel-worktree")),
            store,
            workspace,
            settingsStore);
        var persistence = new ThreadStatePersistenceUseCaseService(settingsStore, store, workspace);
        var turns = new TurnExecutionUseCaseService(
            coordinator,
            new ConversationWorkflowController(store, workspace, new CodexFollowUpQueueWorkspace()),
            lifecycle,
            persistence);

        var cancel = turns.CancelAsync("thread-cancel", "turn-cancel");
        var request = await WaitForRequestAsync(transport, "turn/interrupt");
        Assert.Equal("thread-cancel", request["params"]?["threadId"]?.GetValue<string>());
        Assert.Equal("turn-cancel", request["params"]?["turnId"]?.GetValue<string>());
        transport.ServerSend($"{{\"id\":{request["id"]!.ToJsonString()},\"result\":{{}}}}");
        await cancel;
        await coordinator.DisposeAsync();
    }

    private static AppServerSessionCoordinator CreateCoordinator(FakeAppServerTransport transport) => new(
        new FakeCodexProcessService(transport),
        new TestLogger(),
        new CodexAppServerClientMetadata("turn-use-case-tests", "Turn Use Case Tests", "1.0"));

    private static async Task ConnectAsync(
        AppServerSessionCoordinator coordinator,
        FakeAppServerTransport transport)
    {
        var connect = coordinator.EnsureConnectedAsync(new CodexInstallation(
            true,
            @"C:\Tools\codex.exe",
            "codex test",
            "Codex test",
            "Test installation"));
        var initialize = await WaitForRequestAsync(transport, "initialize");
        transport.ServerSend($"{{\"id\":{initialize["id"]!.ToJsonString()},\"result\":{{\"userAgent\":\"test\",\"platformFamily\":\"windows\",\"platformOs\":\"windows\"}}}}");
        await connect;
    }

    private static async Task<JsonObject> WaitForRequestAsync(
        FakeAppServerTransport transport,
        string method)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var messageCount = transport.ClientMessages.Count;
            for (var index = 0; index < messageCount; index++)
            {
                var request = JsonNode.Parse(transport.ClientMessages[index])?.AsObject();
                if (string.Equals(request?["method"]?.GetValue<string>(), method, StringComparison.Ordinal))
                {
                    return request!;
                }
            }
            await Task.Delay(20, timeout.Token);
        }
    }
}
