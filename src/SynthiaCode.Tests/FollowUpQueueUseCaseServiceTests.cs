using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Infrastructure.Codex;
using Xunit;

public sealed class FollowUpQueueUseCaseServiceTests
{
    [Fact]
    public async Task Enqueue_persists_and_returns_a_detached_snapshot()
    {
        await using var transport = new FakeAppServerTransport();
        var context = CreateContext(transport, new FakeSettingsStore(), "queue-detached");

        var result = await context.Queue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
            context.Settings,
            context.ThreadId,
            "original",
            new QueuedTurnOptionsSnapshot { WorkspacePath = Path.GetTempPath() },
            [],
            []));
        result.Snapshots.Single().Text = "mutated";

        Assert.Equal("original", context.Queue.GetSnapshots(context.ThreadId).Single().Text);
        Assert.Equal("original", context.Settings.ProjectThreads.Single().QueuedFollowUps.Single().Text);
        await context.HarnessRuntime.DisposeAsync();
        await context.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_marks_attention_when_initial_persistence_fails_without_starting_remote_turn()
    {
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new ToggleFailSettingsStore(failFromSave: 2);
        var context = CreateContext(transport, settingsStore, "queue-save-failure");
        await context.Queue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
            context.Settings,
            context.ThreadId,
            "queued",
            new QueuedTurnOptionsSnapshot { WorkspacePath = Path.GetTempPath() },
            [],
            []));
        var prepareCalls = 0;

        var result = await context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            (item, _) =>
            {
                prepareCalls++;
                return Task.FromResult(CreatePreparedTurn(context.ThreadId, item));
            }));

        Assert.True(result.Dispatch.Attempted);
        Assert.False(result.Dispatch.RemoteTurnStarted);
        Assert.Equal(0, prepareCalls);
        Assert.Equal(QueuedFollowUpState.NeedsAttention, context.Queue.GetSnapshots(context.ThreadId).Single().State);
        Assert.DoesNotContain(transport.ClientMessages, message =>
            string.Equals(JsonNode.Parse(message)?["method"]?.GetValue<string>(), "turn/start", StringComparison.Ordinal));
        await context.Queue.DisposeAsync();
        await context.HarnessRuntime.DisposeAsync();
        await context.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_starts_the_head_item_and_durably_removes_it()
    {
        await using var transport = new FakeAppServerTransport();
        var context = CreateContext(transport, new FakeSettingsStore(), "queue-success");
        await ConnectAsync(context.Coordinator, transport);
        await context.Queue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
            context.Settings,
            context.ThreadId,
            "queued",
            new QueuedTurnOptionsSnapshot { WorkspacePath = Path.GetTempPath() },
            [],
            []));

        var dispatch = context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            (item, _) => Task.FromResult(CreatePreparedTurn(context.ThreadId, item))));
        var request = await WaitForRequestAsync(transport, "turn/start");
        transport.ServerSend($"{{\"id\":{request["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-queue\"}}}}}}");

        var result = await dispatch;
        Assert.True(result.Dispatch.RemoteTurnStarted);
        Assert.Equal("turn-queue", result.Dispatch.TurnId);
        Assert.Empty(context.Queue.GetSnapshots(context.ThreadId));
        Assert.Empty(context.Settings.ProjectThreads.Single().QueuedFollowUps);
        Assert.True(context.Conversations.IsRunning(context.ThreadId));
        await context.Queue.DisposeAsync();
        await context.HarnessRuntime.DisposeAsync();
        await context.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_awaits_preflight_immediately_before_harness_start()
    {
        await using var transport = new FakeAppServerTransport();
        var context = CreateContext(transport, new FakeSettingsStore(), "queue-preflight-order");
        await ConnectAsync(context.Coordinator, transport);
        await context.Queue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
            context.Settings,
            context.ThreadId,
            "queued",
            new QueuedTurnOptionsSnapshot { WorkspacePath = Path.GetTempPath() },
            [],
            []));
        var calls = new List<string>();
        var releasePreflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatch = context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            async (item, cancellationToken) =>
            {
                calls.Add("preflight");
                await releasePreflight.Task.WaitAsync(cancellationToken);
                return CreatePreparedTurn(context.ThreadId, item);
            }));

        await WaitUntilAsync(() => calls.Count == 1);
        Assert.Equal(["preflight"], calls);
        Assert.DoesNotContain(transport.ClientMessages, message =>
            string.Equals(JsonNode.Parse(message)?["method"]?.GetValue<string>(), "turn/start", StringComparison.Ordinal));

        releasePreflight.SetResult();
        var request = await WaitForRequestAsync(transport, "turn/start");
        Assert.Equal(["preflight"], calls);
        transport.ServerSend($"{{\"id\":{request["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-preflight\"}}}}}}");

        var result = await dispatch;
        Assert.True(result.Dispatch.RemoteTurnStarted);
        await context.Queue.DisposeAsync();
        await context.HarnessRuntime.DisposeAsync();
        await context.Coordinator.DisposeAsync();
    }

    private static QueueTestContext CreateContext(
        FakeAppServerTransport transport,
        ISettingsStore settingsStore,
        string threadId)
    {
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            new TestLogger(),
            new CodexAppServerClientMetadata("queue-use-case-tests", "Queue Use Case Tests", "1.0"));
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var queues = new CodexFollowUpQueueWorkspace();
        var conversations = new ConversationWorkflowController(store, workspace, queues);
        var installation = new CodexInstallation(
            true,
            @"C:\Tools\codex.exe",
            "codex test",
            "Codex test",
            "Test installation");
        var harnessRuntime = new HarnessRuntimeCoordinator(new HarnessRegistry([
            new CodexHarness(new FakeCodexDiscoveryService(installation), coordinator)
        ]));
        var queue = new FollowUpQueueUseCaseService(
            new HarnessOperations(harnessRuntime),
            conversations,
            settingsStore,
            workspace,
            queues);
        var settings = new AppSettings();
        var state = new ProjectThreadState
        {
            ThreadId = threadId,
            ScopeKind = ThreadScopeKind.General,
            WorkspacePath = Path.GetTempPath(),
            Title = "Queue",
            Preview = string.Empty
        };
        store.Upsert(settings, state);
        conversations.RegisterCreated(state);
        return new QueueTestContext(coordinator, harnessRuntime, conversations, queue, settings, threadId);
    }

    private static PreparedHarnessTurn CreatePreparedTurn(
        string threadId,
        QueuedFollowUpSnapshot item)
    {
        var address = new ConversationAddress(
            new ConversationId(AppSettingsHarnessMigration.CreateDeterministicConversationId(
                KnownHarnessIds.Codex,
                threadId)),
            HarnessId.Codex,
            threadId);
        return new PreparedHarnessTurn(
            new HarnessConnectionOptions(item.Options.WorkspacePath),
            new StartTurnCommand(
                address,
                [new TextContentPart(item.Text)],
                item.Options.WorkspacePath,
                HarnessTurnOptions.Default));
    }

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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed record QueueTestContext(
        AppServerSessionCoordinator Coordinator,
        HarnessRuntimeCoordinator HarnessRuntime,
        ConversationWorkflowController Conversations,
        FollowUpQueueUseCaseService Queue,
        AppSettings Settings,
        string ThreadId);

    private sealed class ToggleFailSettingsStore(int failFromSave) : ISettingsStore
    {
        private int saves;
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "SynthiaCode.Tests", "queue-failure.json");
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref saves) >= failFromSave)
            {
                throw new IOException("planned persistence failure");
            }
            return Task.CompletedTask;
        }
    }
}
