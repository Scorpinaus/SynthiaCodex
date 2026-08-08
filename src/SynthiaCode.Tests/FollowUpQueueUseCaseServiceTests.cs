using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
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
        var queued = Assert.Single(context.Queue.GetSnapshots(context.ThreadId));

        var result = await context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            FollowUpDispatchPreparation.Ready(
                queued.Id,
                CreatePreparedTurn(context.ThreadId, queued))));

        Assert.True(result.Dispatch.Attempted);
        Assert.False(result.Dispatch.RemoteTurnStarted);
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
        var queued = Assert.Single(context.Queue.GetSnapshots(context.ThreadId));

        var dispatch = context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            FollowUpDispatchPreparation.Ready(
                queued.Id,
                CreatePreparedTurn(context.ThreadId, queued))));
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

    [Fact(DisplayName = "inline review comments dispatch exact queued prompt")]
    public async Task Dispatch_uses_effective_inline_review_prompt_in_local_transcript()
    {
        await using var transport = new FakeAppServerTransport();
        var context = CreateContext(transport, new FakeSettingsStore(), "queue-inline-comment");
        await ConnectAsync(context.Coordinator, transport);
        var root = Path.GetFullPath(Path.GetTempPath());
        var comment = GitInlineComment.Create(
            root,
            "src/App.cs",
            null,
            GitDiffSide.New,
            8,
            "return value;",
            "Use the validated value.");
        await context.Queue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
            context.Settings,
            context.ThreadId,
            string.Empty,
            new QueuedTurnOptionsSnapshot { WorkspacePath = root },
            [],
            [],
            [comment]));
        var queued = Assert.Single(context.Queue.GetSnapshots(context.ThreadId));
        var effectivePrompt = GitInlineCommentPromptFormatter.AppendToPrompt(string.Empty, [comment]);

        var dispatch = context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            FollowUpDispatchPreparation.Ready(
                queued.Id,
                new PreparedHarnessTurn(
                    new HarnessConnectionOptions(queued.Options.WorkspacePath),
                    new StartTurnCommand(
                        new ConversationAddress(
                            new ConversationId(AppSettingsHarnessMigration.CreateDeterministicConversationId(
                                KnownHarnessIds.Codex,
                                context.ThreadId)),
                            HarnessId.Codex,
                            context.ThreadId),
                        [new TextContentPart(effectivePrompt)],
                        queued.Options.WorkspacePath,
                        HarnessTurnOptions.Default),
                    effectivePrompt))));
        var request = await WaitForRequestAsync(transport, "turn/start");
        transport.ServerSend($"{{\"id\":{request["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-inline-comment\"}}}}}}");

        var result = await dispatch;
        var turn = context.Conversations.GetSnapshot(context.ThreadId).ConversationTurns.Single();
        Assert.True(result.Dispatch.RemoteTurnStarted);
        Assert.Equal(effectivePrompt, turn.UserPrompt);
        Assert.Contains("Use the validated value.", turn.UserPrompt, StringComparison.Ordinal);
        Assert.Empty(context.Queue.GetSnapshots(context.ThreadId));
        await context.Queue.DisposeAsync();
        await context.HarnessRuntime.DisposeAsync();
        await context.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_rejects_a_stale_preparation_before_harness_start()
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
        var queued = Assert.Single(context.Queue.GetSnapshots(context.ThreadId));

        var result = await context.Queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
            context.Settings,
            context.ThreadId,
            FollowUpDispatchPreparation.Ready(
                "stale-follow-up-id",
                CreatePreparedTurn(context.ThreadId, queued))));

        Assert.False(result.Dispatch.Attempted);
        Assert.Single(context.Queue.GetSnapshots(context.ThreadId));
        Assert.DoesNotContain(transport.ClientMessages, message =>
            string.Equals(JsonNode.Parse(message)?["method"]?.GetValue<string>(), "turn/start", StringComparison.Ordinal));
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
