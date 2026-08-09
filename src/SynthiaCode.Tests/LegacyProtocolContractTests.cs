using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Infrastructure.Auth;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Git;
using SynthiaCode.Infrastructure.Logging;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Settings;
using SynthiaCode.Infrastructure.Terminal;
using SynthiaCode.Infrastructure.Worktrees;
using SynthiaCode.Infrastructure.Workspaces;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

[Trait("Category", TestCategories.ProtocolContract)]
[Collection(TestCategories.NativeCollection)]
public sealed class LegacyProtocolContractTests : LegacyRuntimeTestSupport
{
    [Fact(DisplayName = "app-server client writes initialize handshake")]
    public async Task TestAppServerInitializeWritesHandshakeAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());

        var initializeTask = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);

        var initialize = ParseMessage(transport.ClientMessages[0]);
        AssertJsonString("initialize", initialize, "method", "initialize method");
        AssertJsonInt(0, initialize, "id", "initialize id");
        AssertJsonString("synthiacode", initialize, "params.clientInfo.name", "client info name");
        AssertJsonString("SynthiaCode", initialize, "params.clientInfo.title", "client info title");

        var initialized = ParseMessage(transport.ClientMessages[1]);
        AssertJsonString("initialized", initialized, "method", "initialized method");
        AssertTrue(!initialized.AsObject().ContainsKey("id"), "initialized notification has no id");

        transport.ServerSend(
            """
            {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
            """);

        var session = await initializeTask;
        AssertEqual("codex-test", session.UserAgent, "initialize user agent");
        AssertEqual("windows", session.PlatformFamily, "initialize platform family");
    }

    [Fact(DisplayName = "app-server process transport preserves UTF-8")]
    public async Task TestAppServerProcessTransportPreservesUtf8Async()
    {
        using var temp = TempWorkspace.Create();
        var startInfoFactory = typeof(CodexAppServerProcessTransport).GetMethod(
            "CreateStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("transport start-info factory was not found");
        var inspected = (ProcessStartInfo?)startInfoFactory.Invoke(null, ["codex.exe"])
            ?? throw new InvalidOperationException("transport start-info factory returned null");
        AssertUtf8ProtocolEncoding(inspected.StandardInputEncoding, "standard input");
        AssertUtf8ProtocolEncoding(inspected.StandardOutputEncoding, "standard output");
        AssertUtf8ProtocolEncoding(inspected.StandardErrorEncoding, "standard error");

        var fixtureAssemblyPath = typeof(UnicodeEchoFixtureMarker).Assembly.Location;
        var fixtureExecutablePath = Path.ChangeExtension(fixtureAssemblyPath, ".exe");
        var fixtureCommand = File.Exists(fixtureExecutablePath)
            ? $"\"{fixtureExecutablePath}\""
            : $"dotnet \"{fixtureAssemblyPath}\"";
        var fixturePath = Path.Combine(temp.Root, "unicode-fixture.cmd");
        await File.WriteAllTextAsync(
            fixturePath,
            $"@echo off{Environment.NewLine}{fixtureCommand}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await using var transport = new CodexAppServerProcessTransport(fixturePath, new TestLogger());
        await transport.StartAsync();
        var payload = "{\"text\":\"I\u2019m ready\u2014now\u2026 \u0395\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac \u65e5\u672c\u8a9e \ud83d\ude80\"}";
        await transport.WriteLineAsync(payload);

        string? echoed = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var line in transport.ReadLinesAsync(timeout.Token))
        {
            echoed = line;
            break;
        }

        AssertEqual(payload, echoed, "UTF-8 protocol round trip");
    }

    [Fact(DisplayName = "app-server process transport uses isolated home")]
    public async Task TestAppServerProcessTransportUsesIsolatedHomeAsync()
    {
        using var temp = TempWorkspace.Create();
        var fixturePath = Path.Combine(temp.Root, "codex-home-fixture.cmd");
        await File.WriteAllTextAsync(
            fixturePath,
            $"@echo off{Environment.NewLine}echo %CODEX_HOME%{Environment.NewLine}echo %RUST_LOG%{Environment.NewLine}");
        var runtimeEnvironment = new CodexRuntimeEnvironment(Path.Combine(temp.Root, "isolated-home"));
        var processRustLog = Environment.GetEnvironmentVariable("RUST_LOG");

        await using var transport = new CodexAppServerProcessTransport(
            fixturePath,
            new TestLogger(),
            runtimeEnvironment);
        await transport.StartAsync();

        var reportedEnvironment = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var line in transport.ReadLinesAsync(timeout.Token))
        {
            reportedEnvironment.Add(line);
            if (reportedEnvironment.Count == 2)
            {
                break;
            }
        }

        AssertEqual(2, reportedEnvironment.Count, "app-server child environment output count");
        AssertEqual(runtimeEnvironment.HomePath, reportedEnvironment[0], "app-server child CODEX_HOME");
        AssertEqual("warn", reportedEnvironment[1], "app-server child RUST_LOG");
        AssertEqual(processRustLog, Environment.GetEnvironmentVariable("RUST_LOG"), "process RUST_LOG is unchanged");
    }

    [Fact(DisplayName = "app-server client serializes initialize writes")]
    public async Task TestAppServerClientSerializesInitializeWritesAsync()
    {
        await using var transport = new SlowWriteAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());

        var initializeTask = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend(
            """
            {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
            """);

        var session = await initializeTask;

        AssertEqual("codex-test", session.UserAgent, "serialized initialize user agent");
        AssertTrue(!transport.OverlappingWriteDetected, "transport writes did not overlap");
    }

    [Fact(DisplayName = "app-server client starts thread and turn")]
    public async Task TestAppServerStartsThreadAndTurnAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);

        var threadTask = client.StartThreadAsync(CodexThreadStartOptions.Default);
        await transport.WaitForClientMessageCountAsync(3);

        var threadRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("thread/start", threadRequest, "method", "thread start method");
        AssertJsonInt(1, threadRequest, "id", "thread start id");
        AssertTrue(threadRequest["params"] is not null, "thread start params object");

        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"thr_123"}}}
            """);

        var thread = await threadTask;
        AssertEqual("thr_123", thread.ThreadId, "thread id");

        var cwd = Path.Combine("D:\\", "Repo With Space");
        var turnTask = client.StartTurnAsync(new CodexTurnStartRequest(
            thread.ThreadId,
            "Summarize this repo.",
            cwd,
            CodexSandbox.WorkspaceWrite));

        await transport.WaitForClientMessageCountAsync(4);

        var turnRequest = ParseMessage(transport.ClientMessages[3]);
        AssertJsonString("turn/start", turnRequest, "method", "turn start method");
        AssertJsonInt(2, turnRequest, "id", "turn start id");
        AssertJsonString("thr_123", turnRequest, "params.threadId", "turn thread id");
        AssertJsonString(cwd, turnRequest, "params.cwd", "turn cwd");
        AssertJsonString("workspaceWrite", turnRequest, "params.sandboxPolicy.type", "turn sandbox policy");
        AssertTrue(ResolvePath(turnRequest, "params.sandbox") is null, "turn sandbox legacy field is omitted");
        AssertJsonString("text", turnRequest, "params.input.0.type", "turn input type");
        AssertJsonString("Summarize this repo.", turnRequest, "params.input.0.text", "turn input text");

        transport.ServerSend(
            """
            {"id":2,"result":{"turn":{"id":"turn_456"}}}
            """);

        var turn = await turnTask;
        AssertEqual("turn_456", turn.TurnId, "turn id");
    }

    [Fact(DisplayName = "app-server client sends model and reasoning overrides")]
    public async Task TestAppServerSendsModelAndReasoningOverridesAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);

        var turnTask = client.StartTurnAsync(new CodexTurnStartRequest(
            "thr_123",
            "Summarize this repo.",
            Path.Combine("D:\\", "Repo"),
            CodexSandbox.WorkspaceWrite,
            "gpt-test",
            CodexReasoningEffort.High));

        await transport.WaitForClientMessageCountAsync(3);

        var turnRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("turn/start", turnRequest, "method", "override turn method");
        AssertJsonString("gpt-test", turnRequest, "params.model", "turn model override");
        AssertJsonString("high", turnRequest, "params.effort", "turn reasoning effort");

        transport.ServerSend(
            """
            {"id":1,"result":{"turn":{"id":"turn_456"}}}
            """);

        var turn = await turnTask;
        AssertEqual("turn_456", turn.TurnId, "override turn id");
    }

    [Fact(DisplayName = "app-server client resumes thread")]
    public async Task TestAppServerResumesThreadAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);

        var cwd = Path.Combine("D:\\", "Repo With Space");
        var resumeTask = client.ResumeThreadAsync(new CodexThreadResumeRequest(
            "thr_existing",
            cwd,
            CodexSandbox.WorkspaceWrite));

        await transport.WaitForClientMessageCountAsync(3);

        var resumeRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("thread/resume", resumeRequest, "method", "thread resume method");
        AssertJsonInt(1, resumeRequest, "id", "thread resume id");
        AssertJsonString("thr_existing", resumeRequest, "params.threadId", "resume thread id");
        AssertJsonString(cwd, resumeRequest, "params.cwd", "resume cwd");
        AssertJsonString("workspace-write", resumeRequest, "params.sandbox", "resume sandbox");

        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"thr_existing"}}}
            """);

        var resumed = await resumeTask;
        AssertEqual("thr_existing", resumed.ThreadId, "resumed thread id");
    }

    [Fact(DisplayName = "app-server client sends lifecycle requests")]
    public async Task TestAppServerLifecycleRequestsAsync()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);

        var listTask = client.ListThreadsAsync(new CodexThreadListRequest(@"C:\Repo", Archived: false, Limit: 25));
        await transport.WaitForClientMessageCountAsync(3);
        var list = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("thread/list", list, "method", "thread list method");
        AssertJsonString(@"C:\Repo", list, "params.cwd", "thread list cwd");
        transport.ServerSend(
            """
            {"id":1,"result":{"data":[{"id":"thr_a","name":"First thread","preview":"First prompt","cwd":"C:\\Repo","createdAt":100,"updatedAt":200,"status":{"type":"idle"}}],"nextCursor":"next"}}
            """);
        var page = await listTask;
        AssertEqual(1, page.Threads.Count, "listed thread count");
        AssertEqual("thr_a", page.Threads[0].ThreadId, "listed thread id");
        AssertEqual("First thread", page.Threads[0].Title, "listed thread title");
        AssertEqual("next", page.NextCursor, "thread list cursor");

        var forkTask = client.ForkThreadAsync(new CodexThreadForkRequest("thr_a", @"C:\Repo", CodexSandbox.WorkspaceWrite));
        await transport.WaitForClientMessageCountAsync(4);
        var fork = ParseMessage(transport.ClientMessages[3]);
        AssertJsonString("thread/fork", fork, "method", "thread fork method");
        AssertJsonString("thr_a", fork, "params.threadId", "fork source id");
        AssertTrue(
            fork["params"] is JsonObject forkParams && !forkParams.ContainsKey("lastTurnId"),
            "full thread fork omits lastTurnId");
        transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_fork"}}}""");
        AssertEqual("thr_fork", (await forkTask).ThreadId, "forked thread id");

        var archiveTask = client.ArchiveThreadAsync("thr_a");
        await transport.WaitForClientMessageCountAsync(5);
        AssertJsonString("thread/archive", ParseMessage(transport.ClientMessages[4]), "method", "archive method");
        transport.ServerSend("""{"id":3,"result":{}}""");
        await archiveTask;

        var unarchiveTask = client.UnarchiveThreadAsync("thr_a");
        await transport.WaitForClientMessageCountAsync(6);
        AssertJsonString("thread/unarchive", ParseMessage(transport.ClientMessages[5]), "method", "unarchive method");
        transport.ServerSend("""{"id":4,"result":{"thread":{"id":"thr_a"}}}""");
        await unarchiveTask;

        var steerTask = client.SteerTurnAsync(new CodexTurnSteerRequest("thr_a", "turn_1", "Focus on tests."));
        await transport.WaitForClientMessageCountAsync(7);
        var steer = ParseMessage(transport.ClientMessages[6]);
        AssertJsonString("turn/steer", steer, "method", "turn steer method");
        AssertJsonString("turn_1", steer, "params.expectedTurnId", "steer turn precondition");
        AssertJsonString("Focus on tests.", steer, "params.input.0.text", "steer text");
        transport.ServerSend("""{"id":5,"result":{"turnId":"turn_1"}}""");
        AssertEqual("turn_1", (await steerTask).TurnId, "steered turn id");
    }

    [Fact(DisplayName = "app-server initialize advertises notification opt outs")]
    public async Task TestAppServerInitializeCapabilitiesAsync()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());

        var initializeTask = client.InitializeAsync(new CodexInitializeOptions(
            ExperimentalApi: false,
            OptOutNotificationMethods: ["thread/tokenUsage/updated"]));
        await transport.WaitForClientMessageCountAsync(2);

        var initialize = ParseMessage(transport.ClientMessages[0]);
        AssertEqual(false, ResolvePath(initialize, "params.capabilities.experimentalApi")!.GetValue<bool>(), "experimental capability");
        AssertJsonString(
            "thread/tokenUsage/updated",
            initialize,
            "params.capabilities.optOutNotificationMethods.0",
            "notification opt out");

        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await initializeTask;
    }

    [Fact(DisplayName = "app-server client reports connection failure")]
    public async Task TestAppServerConnectionFailureAsync()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);
        AppServerConnectionFailedEventArgs? failure = null;
        client.ConnectionFailed += (_, args) => failure = args;

        var pending = client.ListThreadsAsync(new CodexThreadListRequest(@"C:\Repo"));
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerFail(new IOException("fake app-server crash"));

        var requestFailed = false;
        try
        {
            await pending;
        }
        catch (IOException ex) when (ex.Message.Contains("fake app-server crash", StringComparison.Ordinal))
        {
            requestFailed = true;
        }

        await StateProbe.WaitForAsync(() => failure is not null, "connection failure event");
        AssertTrue(requestFailed, "pending request failed after connection crash");
        AssertTrue(!client.IsHealthy, "client health after connection crash");
        AssertTrue(failure!.Exception.Message.Contains("fake app-server crash", StringComparison.Ordinal), "connection failure detail");
    }

    [Fact(DisplayName = "app-server client lists models")]
    public async Task TestAppServerListsModelsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);

        var modelsTask = client.ListModelsAsync();
        await transport.WaitForClientMessageCountAsync(3);

        var modelsRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("model/list", modelsRequest, "method", "model list method");
        AssertJsonInt(1, modelsRequest, "id", "model list id");

        transport.ServerSend(
            """
            {"id":1,"result":{"data":[{"id":"default","model":"gpt-default","displayName":"GPT Default","isDefault":true,"supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"Balanced"}]},{"id":"fast","model":"gpt-fast","displayName":"GPT Fast","isDefault":false,"supportedReasoningEfforts":[{"reasoningEffort":"minimal","description":"Fast"}]}]}}
            """);

        var models = await modelsTask;
        AssertEqual(2, models.Count, "model count");
        AssertEqual("gpt-default", models[0].Model, "first model id");
        AssertEqual("GPT Default", models[0].DisplayName, "first model display");
        AssertTrue(models[0].IsDefault, "first model default");
        AssertEqual(CodexReasoningEffort.Medium, models[0].SupportedReasoningEfforts[0].Effort, "first model effort");
    }

    [Fact(DisplayName = "app-server notifications update thread state")]
    public Task TestAppServerNotificationsUpdateThreadStateAsync()
    {
        var service = new CodexThreadService();

        service.ApplyNotification(Notification(
            "turn/started",
            """
            {"turn":{"id":"turn_456"}}
            """));
        service.ApplyNotification(Notification(
            "item/started",
            """
            {"item":{"type":"command","command":"dotnet test"}}
            """));
        service.ApplyNotification(Notification(
            "item/agentMessage/delta",
            """
            {"delta":"Hello "}
            """));
        service.ApplyNotification(Notification(
            "item/agentMessage/delta",
            """
            {"delta":"world"}
            """));
        service.ApplyNotification(Notification(
            "turn/completed",
            """
            {"turn":{"id":"turn_456"},"status":"completed"}
            """));

        AssertEqual(CodexTurnStatus.Completed, service.ActiveTurnStatus, "turn status");
        AssertEqual("turn_456", service.ActiveTurnId, "active turn id");
        AssertEqual("Hello world", service.FinalResponse, "final response");
        AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.TurnStarted), "turn started timeline item");
        AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.CommandStarted), "command started timeline item");
        AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.AgentMessageDelta), "agent delta timeline item");
        AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.TurnCompleted), "turn completed timeline item");

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "notification batcher preserves long stream output and order")]
    public Task TestNotificationBatcherPreservesLongStreamOutputAndOrderAsync()
    {
        const int deltaCount = 25_000;
        var emitted = new List<AppServerNotification>();
        using var batcher = new AppServerNotificationBatcher(emitted.Add, TimeSpan.FromSeconds(30));
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();

        for (var index = 0; index < deltaCount; index++)
        {
            batcher.Enqueue(new AppServerNotification(
                "item/agentMessage/delta",
                new JsonObject
                {
                    ["threadId"] = "thread_long",
                    ["turnId"] = "turn_long",
                    ["itemId"] = "item_long",
                    ["delta"] = "x"
                }));
        }

        batcher.Enqueue(new AppServerNotification(
            "turn/completed",
            new JsonObject
            {
                ["threadId"] = "thread_long",
                ["turn"] = new JsonObject { ["id"] = "turn_long", ["status"] = "completed" }
            }));

        timer.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var metrics = batcher.Metrics;
        AssertEqual(deltaCount + 1L, metrics.ReceivedCount, "long stream received count");
        AssertEqual(2L, metrics.EmittedCount, "long stream emitted batch count");
        AssertEqual("item/agentMessage/delta", emitted[0].Method, "long stream delta emitted before completion");
        AssertEqual("turn/completed", emitted[1].Method, "long stream completion order");

        var service = new CodexThreadService();
        foreach (var notification in emitted)
        {
            service.ApplyNotification(CodexAppServerNotification.Decode(notification));
        }

        AssertEqual(deltaCount, service.FinalResponse.Length, "long stream final response length");
        AssertEqual(CodexTurnStatus.Completed, service.ActiveTurnStatus, "long stream completion state");
        Console.WriteLine(
            $"METRIC Codex stream: {metrics.ReceivedCount} notifications -> {metrics.EmittedCount} UI batches, " +
            $"{allocatedBytes / (1024d * 1024d):F2} MiB allocated in {timer.Elapsed.TotalMilliseconds:F2} ms");

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "notification batcher flushes idle deltas")]
    public async Task TestNotificationBatcherFlushesIdleDeltasAsync()
    {
        var clock = new ManualTimeProvider();
        var messages = new MessageProbe<AppServerNotification>();
        using var batcher = new AppServerNotificationBatcher(
            messages.Publish,
            TimeSpan.FromMilliseconds(20),
            clock);
        batcher.Enqueue(new AppServerNotification(
            "item/agentMessage/delta",
            new JsonObject { ["delta"] = "visible" }));

        var emittedTask = messages.WaitForAsync(notification =>
            notification.Method == "item/agentMessage/delta");
        clock.Advance(TimeSpan.FromMilliseconds(20));
        var emitted = await emittedTask;
        AssertEqual("visible", emitted.Params["delta"]?.GetValue<string>(), "idle delta text");
    }

    [Fact(DisplayName = "thread service bounds live streamed history")]
    public Task TestThreadServiceBoundsLiveStreamedHistoryAsync()
    {
        var service = new CodexThreadService();
        var overflow = 25;
        var totalNotifications = CodexThreadService.MaximumRawEvents + overflow;

        for (var index = 0; index < totalNotifications; index++)
        {
            service.ApplyNotification(Notification(
                "test/progress",
                $$"""
                {"message":"event {{index}}"}
                """));
        }

        AssertEqual(CodexThreadService.MaximumRawEvents, service.RawEvents.Count, "raw event live bound");
        AssertEqual(CodexThreadService.MaximumTimelineItems, service.TimelineItems.Count, "timeline live bound");
        AssertTrue(service.RawEvents[0].Contains($"event {overflow}", StringComparison.Ordinal), "old raw events are evicted");
        AssertEqual($"event {overflow}", service.TimelineItems[0].Detail, "old timeline items are evicted");
        AssertTrue(service.RawEvents[^1].Contains($"event {totalNotifications - 1}", StringComparison.Ordinal), "latest raw event is retained");
        AssertEqual($"event {totalNotifications - 1}", service.TimelineItems[^1].Detail, "latest timeline item is retained");

        var restored = new CodexThreadService();
        restored.Restore(
            "thread_restored",
            string.Empty,
            Enumerable.Range(0, totalNotifications).Select(index => new CodexTimelineItem(
                CodexTimelineItemKind.Raw,
                "Restored",
                $"restored {index}",
                "test/restore",
                DateTimeOffset.UnixEpoch.AddSeconds(index))),
            Enumerable.Range(0, totalNotifications).Select(index => $"restored raw {index}"));

        AssertEqual(CodexThreadService.MaximumTimelineItems, restored.TimelineItems.Count, "restored timeline bound");
        AssertEqual(CodexThreadService.MaximumRawEvents, restored.RawEvents.Count, "restored raw event bound");
        AssertEqual($"restored {overflow}", restored.TimelineItems[0].Detail, "restore evicts oldest timeline items");
        AssertEqual($"restored raw {overflow}", restored.RawEvents[0], "restore evicts oldest raw events");

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "app-server v2 notifications update thread state")]
    public Task TestAppServerV2NotificationsUpdateThreadStateAsync()
    {
        var service = new CodexThreadService();

        service.ApplyNotification(Notification(
            "item/completed",
            """
            {"item":{"type":"agentMessage","text":"PHASE1_SMOKE_OK"},"threadId":"thr_123","turnId":"turn_456"}
            """));
        service.ApplyNotification(Notification(
            "turn/completed",
            """
            {"threadId":"thr_123","turn":{"id":"turn_456","status":"failed","error":{"message":"stream disconnected before completion","additionalDetails":"missing auth"},"items":[]}}
            """));

        AssertEqual(CodexTurnStatus.Failed, service.ActiveTurnStatus, "v2 failed turn status");
        AssertEqual("turn_456", service.ActiveTurnId, "v2 active turn id");
        AssertEqual("PHASE1_SMOKE_OK", service.FinalResponse, "v2 final response");
        AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.AgentMessage), "v2 agent message timeline item");
        AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.TurnCompleted && item.Detail.Contains("stream disconnected", StringComparison.Ordinal)), "v2 failed turn detail");

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "app-server error notifications show detail")]
    public Task TestAppServerErrorNotificationsShowDetailAsync()
    {
        var service = new CodexThreadService();

        service.ApplyNotification(Notification(
            "error",
            """
            {"error":{"message":"Reconnecting... 2/5","additionalDetails":"unexpected status 401 Unauthorized"},"willRetry":true,"threadId":"thr_123","turnId":"turn_456"}
            """));

        var error = service.TimelineItems.Single(item => item.Kind == CodexTimelineItemKind.Error);
        AssertTrue(error.Detail.Contains("Reconnecting", StringComparison.Ordinal), "error detail includes message");
        AssertTrue(error.Detail.Contains("401", StringComparison.Ordinal), "error detail includes additional details");

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "thread workspace routes parallel notifications")]
    public Task TestThreadWorkspaceRoutesParallelNotificationsAsync()
    {
        var workspace = new CodexThreadWorkspace();
        workspace.Restore(new ProjectThreadState { ProjectPath = @"C:\Repo", ThreadId = "thr_a" });
        workspace.Restore(new ProjectThreadState { ProjectPath = @"C:\Repo", ThreadId = "thr_b" });

        workspace.ApplyNotification(Notification(
            "item/agentMessage/delta",
            """{"threadId":"thr_a","turnId":"turn_a","delta":"alpha"}"""));
        workspace.ApplyNotification(Notification(
            "item/agentMessage/delta",
            """{"threadId":"thr_b","turnId":"turn_b","delta":"beta"}"""));

        AssertEqual("alpha", workspace.GetRequired("thr_a").FinalResponse, "first parallel response");
        AssertEqual("beta", workspace.GetRequired("thr_b").FinalResponse, "second parallel response");
        AssertEqual(1, workspace.GetRequired("thr_a").RawEvents.Count, "first parallel event count");
        AssertEqual(1, workspace.GetRequired("thr_b").RawEvents.Count, "second parallel event count");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "app-server cancellation sends turn interrupt")]
    public async Task TestAppServerCancellationSendsInterruptAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport, TestClientMetadata());
        await CompleteInitializeAsync(client, transport);

        var cancelTask = client.CancelTurnAsync("thr_123", "turn_456");
        await transport.WaitForClientMessageCountAsync(3);

        var cancelRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("turn/interrupt", cancelRequest, "method", "cancel method");
        AssertJsonInt(1, cancelRequest, "id", "cancel id");
        AssertJsonString("thr_123", cancelRequest, "params.threadId", "cancel thread id");
        AssertJsonString("turn_456", cancelRequest, "params.turnId", "cancel turn id");

        transport.ServerSend(
            """
            {"id":1,"result":{"ok":true}}
            """);

        await cancelTask;
    }

    [Fact(DisplayName = "live codex app-server initializes when enabled")]
    public async Task TestLiveCodexAppServerInitializesWhenEnabledAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SYNTHIACODE_RUN_LIVE_CODEX_SMOKE"), "1", StringComparison.Ordinal))
        {
            Console.WriteLine("SKIP live codex app-server smoke test; set SYNTHIACODE_RUN_LIVE_CODEX_SMOKE=1 to run it.");
            return;
        }

        var logger = new TestLogger();
        using var runtimeTemp = TempWorkspace.Create();
        var runtimeEnvironment = new CodexRuntimeEnvironment(Path.Combine(runtimeTemp.Root, "codex-home"));
        var discovery = new CodexDiscoveryService(logger, runtimeEnvironment);
        var installation = await discovery.DetectAsync();

        AssertTrue(installation.IsFound, "live codex installation is found");
        AssertTrue(!string.IsNullOrWhiteSpace(installation.ExecutablePath), "live codex executable path");
        AssertTrue(!string.IsNullOrWhiteSpace(installation.Version), "live codex version");

        var processService = new CodexProcessService(logger, runtimeEnvironment);
        var transport = await processService.StartAppServerTransportAsync(installation);
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("synthiacode_test", "SynthiaCode Test", "0.1.0"));

        var session = await client.InitializeAsync();

        AssertEqual("windows", session.PlatformFamily, "live app-server platform family");
        AssertEqual("windows", session.PlatformOs, "live app-server platform os");
        AssertTrue(session.UserAgent?.Contains("synthiacode_test", StringComparison.Ordinal) == true, "live app-server user agent includes client");

        var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default);
        AssertTrue(!string.IsNullOrWhiteSpace(thread.ThreadId), "live app-server thread id");

        if (string.Equals(Environment.GetEnvironmentVariable("SYNTHIACODE_RUN_LIVE_CODEX_TURN_SMOKE"), "1", StringComparison.Ordinal))
        {
            using var temp = TempWorkspace.Create();
            var models = await client.ListModelsAsync();
            var requestedModel = Environment.GetEnvironmentVariable("SYNTHIACODE_LIVE_CODEX_MODEL");
            var liveModel = !string.IsNullOrWhiteSpace(requestedModel)
                ? requestedModel
                : models.FirstOrDefault(model => string.Equals(model.Model, "gpt-5.4", StringComparison.OrdinalIgnoreCase))?.Model
                  ?? models.FirstOrDefault(model => !model.Model.Contains("5.6", StringComparison.OrdinalIgnoreCase))?.Model;
            Console.WriteLine($"LIVE model options: {string.Join(", ", models.Select(model => model.Model))}");
            Console.WriteLine($"LIVE selected model: {liveModel ?? "Codex default"}");
            var threadService = new CodexThreadService();
            var turnCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.NotificationReceived += (_, notification) =>
            {
                var typedNotification = CodexAppServerNotification.Decode(notification);
                threadService.ApplyNotification(typedNotification);
                if (typedNotification.Kind == CodexAppServerNotificationKind.TurnCompleted)
                {
                    turnCompleted.TrySetResult();
                }
            };

            var turn = await client.StartTurnAsync(new CodexTurnStartRequest(
                thread.ThreadId,
                "Reply with exactly PHASE1_SMOKE_OK. Do not edit files or run commands.",
                temp.Root,
                CodexSandbox.WorkspaceWrite,
                liveModel));

            AssertTrue(!string.IsNullOrWhiteSpace(turn.TurnId), "live app-server turn id");
            await turnCompleted.Task.WaitAsync(TimeSpan.FromMinutes(3));
            AssertEqual(
                CodexTurnStatus.Completed,
                threadService.ActiveTurnStatus,
                $"live app-server turn completed; detail: {threadService.LastErrorDetail ?? "none"}");
        }
    }

}
