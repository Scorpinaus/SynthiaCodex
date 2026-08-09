using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Settings;
using Xunit;

[Trait("Category", TestCategories.InfrastructureIntegration)]
[Collection(TestCategories.NativeCollection)]
public sealed class Phase0GoldenCharacterizationTests
{
    private static readonly JsonSerializerOptions GoldenJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Conversation_reduction_matches_the_phase_0_golden_snapshot()
    {
        var timestamp = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var service = new CodexThreadService();
        service.BeginTurn("Inspect the queue implementation.");
        service.ApplyEvent(new TurnStartedEvent(HarnessId.Codex, "thread-golden", "turn-golden", timestamp));
        service.ApplyEvent(new ActivityChangedEvent(
            HarnessId.Codex,
            "thread-golden",
            "turn-golden",
            new ActivityItem("command-1", ActivityKind.Command, "Ran tests", "dotnet test", timestamp, true),
            timestamp));
        service.ApplyEvent(new AssistantTextDeltaEvent(
            HarnessId.Codex,
            "thread-golden",
            "turn-golden",
            "message-1",
            "streamed answer",
            timestamp));
        service.ApplyEvent(new AssistantMessageCompletedEvent(
            HarnessId.Codex,
            "thread-golden",
            "turn-golden",
            "message-1",
            "final answer",
            "final_answer",
            timestamp));
        service.ApplyEvent(new TurnDiffChangedEvent(
            HarnessId.Codex,
            "thread-golden",
            "turn-golden",
            "diff --git a/file b/file",
            timestamp));
        service.ApplyEvent(new ContextUsageChangedEvent(HarnessId.Codex, "thread-golden", 1_200, 8_000, timestamp));
        service.ApplyEvent(new ContextCompactedEvent(HarnessId.Codex, "thread-golden", "turn-golden", timestamp));
        service.ApplyEvent(new TurnCompletedEvent(
            HarnessId.Codex,
            "thread-golden",
            "turn-golden",
            ConversationTurnStatus.Completed,
            null,
            timestamp));

        var turn = Assert.Single(service.SnapshotConversation());
        AssertGolden(
            """
            {
              "threadId": "thread-golden",
              "turnId": "turn-golden",
              "status": "Completed",
              "finalResponse": "final answer",
              "requiresAuthentication": false,
              "contextTokensUsed": 1200,
              "contextWindowTokens": 8000,
              "contextCompactionCount": 1,
              "timelineTitles": ["Turn started", "Ran tests", "Compacted context", "Turn completed"],
              "rawEvents": [
                "TurnStartedEvent",
                "ActivityChangedEvent",
                "AssistantTextDeltaEvent",
                "AssistantMessageCompletedEvent",
                "TurnDiffChangedEvent",
                "ContextUsageChangedEvent",
                "ContextCompactedEvent",
                "TurnCompletedEvent"
              ],
              "turn": {
                "prompt": "Inspect the queue implementation.",
                "response": "final answer",
                "status": "Completed",
                "diff": "diff --git a/file b/file"
              }
            }
            """,
            new
            {
                ThreadId = service.ActiveThreadId,
                TurnId = service.ActiveTurnId,
                Status = service.ActiveTurnStatus.ToString(),
                service.FinalResponse,
                service.RequiresAuthentication,
                service.ContextTokensUsed,
                service.ContextWindowTokens,
                service.ContextCompactionCount,
                TimelineTitles = service.TimelineItems.Select(item => item.Title).ToArray(),
                RawEvents = service.RawEvents.ToArray(),
                Turn = new
                {
                    Prompt = turn.UserPrompt,
                    Response = turn.AssistantResponse,
                    Status = turn.Status.ToString(),
                    turn.Diff
                }
            });
    }

    [Fact]
    public async Task Queued_dispatch_matches_the_phase_0_golden_snapshot()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("phase0-golden", "Phase 0 Golden Tests", "1.0"));
        var installation = CreateInstallation();
        var harnessRuntime = new HarnessRuntimeCoordinator(new HarnessRegistry([
            new CodexHarness(new FakeCodexDiscoveryService(installation), coordinator)
        ]));
        var threadStore = new ThreadStore();
        var threadWorkspace = new CodexThreadWorkspace();
        var queues = new CodexFollowUpQueueWorkspace();
        var conversations = new ConversationWorkflowController(threadStore, threadWorkspace, queues);
        var settingsStore = new FakeSettingsStore();
        var queue = new FollowUpQueueUseCaseService(
            new HarnessOperations(harnessRuntime),
            conversations,
            settingsStore,
            queues);
        const string threadId = "thread-queue-golden";
        var state = new ProjectThreadState
        {
            ThreadId = threadId,
            ConversationId = AppSettingsHarnessMigration.CreateDeterministicConversationId(
                KnownHarnessIds.Codex,
                threadId),
            HarnessId = KnownHarnessIds.Codex,
            RemoteConversationId = threadId,
            ScopeKind = ThreadScopeKind.General,
            WorkspacePath = temp.Root,
            Title = "Golden queue"
        };
        threadStore.Upsert(settingsStore.SavedSettings, state);
        conversations.RegisterCreated(state);

        try
        {
            await ConnectAsync(coordinator, transport, installation);
            await queue.EnqueueAsync(new FollowUpEnqueueUseCaseRequest(
                settingsStore.SavedSettings,
                threadId,
                "Run the golden queue test.",
                new QueuedTurnOptionsSnapshot { WorkspacePath = temp.Root, WorkspaceRoots = [temp.Root] },
                [],
                []));
            var queued = Assert.Single(queue.GetSnapshots(threadId));
            var dispatchTask = queue.DispatchNextAsync(new FollowUpDispatchUseCaseRequest(
                settingsStore.SavedSettings,
                threadId,
                FollowUpDispatchPreparation.Ready(
                    queued.Id,
                    new PreparedHarnessTurn(
                        new HarnessConnectionOptions(temp.Root),
                        new StartTurnCommand(
                            new ConversationAddress(new ConversationId(state.ConversationId), HarnessId.Codex, threadId),
                            [new TextContentPart(queued.Text)],
                            temp.Root,
                            new HarnessTurnOptions(ExecutionPolicy: new HarnessExecutionPolicy(
                                WorkspaceAccessMode.WorkspaceWrite,
                                ApprovalInteractionMode.Prompt,
                                ":workspace")),
                            [temp.Root])))));
            var request = await WaitForRequestAsync(transport, "turn/start");
            transport.ServerSend(
                $"{{\"id\":{request["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-queue-golden\"}}}}}}");
            var result = await dispatchTask;
            var parameters = request["params"]!.AsObject();

            AssertGolden(
                """
                {
                  "attempted": true,
                  "turnId": "turn-queue-golden",
                  "remoteTurnStarted": true,
                  "runtimeIsRunning": true,
                  "runtimeQueueCount": 0,
                  "persistedQueueCount": 0,
                  "request": {
                    "method": "turn/start",
                    "threadId": "thread-queue-golden",
                    "permissionProfile": ":workspace",
                    "hasLegacySandbox": false,
                    "prompt": "Run the golden queue test."
                  }
                }
                """,
                new
                {
                    result.Dispatch.Attempted,
                    result.Dispatch.TurnId,
                    result.Dispatch.RemoteTurnStarted,
                    RuntimeIsRunning = conversations.IsRunning(threadId),
                    RuntimeQueueCount = queue.GetSnapshots(threadId).Count,
                    PersistedQueueCount = settingsStore.SavedSettings.ProjectThreads.Single().QueuedFollowUps.Count,
                    Request = new
                    {
                        Method = request["method"]?.GetValue<string>(),
                        ThreadId = parameters["threadId"]?.GetValue<string>(),
                        PermissionProfile = parameters["permissionProfile"]?.GetValue<string>(),
                        HasLegacySandbox = parameters.ContainsKey("sandbox") || parameters.ContainsKey("sandboxPolicy"),
                        Prompt = parameters["input"]?[0]?["text"]?.GetValue<string>()
                    }
                });
        }
        finally
        {
            await queue.DisposeAsync();
            await harnessRuntime.DisposeAsync();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconnect_matches_the_phase_0_golden_snapshot()
    {
        await using var firstTransport = new FakeAppServerTransport();
        await using var secondTransport = new FakeAppServerTransport();
        var processService = new SequenceCodexProcessService(firstTransport, secondTransport);
        var logger = new TestLogger();
        await using var coordinator = new AppServerSessionCoordinator(
            processService,
            logger,
            new CodexAppServerClientMetadata("phase0-golden", "Phase 0 Golden Tests", "1.0"));
        var states = new ConcurrentQueue<string>();
        var stateChanges = new MessageProbe<AppServerSessionState>();
        coordinator.StateChanged += (_, args) =>
        {
            states.Enqueue(args.State.ToString());
            stateChanges.Publish(args.State);
        };
        var installation = CreateInstallation();

        await ConnectAsync(coordinator, firstTransport, installation);
        firstTransport.ServerFail(new IOException("golden simulated crash"));
        await stateChanges.WaitForAsync(
            state => state == AppServerSessionState.Reconnecting,
            "reconnecting state");
        await ConnectAsync(coordinator, secondTransport, installation);

        AssertGolden(
            """
            {
              "states": ["Connecting", "Connected", "Reconnecting", "Connected"],
              "startCount": 2,
              "finalState": "Connected",
              "firstTransportDisposed": true,
              "recoveryMetricLogged": true
            }
            """,
            new
            {
                States = states.ToArray(),
                processService.StartCount,
                FinalState = coordinator.State.ToString(),
                FirstTransportDisposed = firstTransport.IsDisposed,
                RecoveryMetricLogged = logger.Entries.Any(entry => entry.EventName == "app_server_recovered")
            });
    }

    [Fact]
    public async Task Persistence_migration_matches_the_phase_0_golden_snapshot()
    {
        using var temp = TempWorkspace.Create();
        var store = new JsonSettingsStore(temp.Root, new TestLogger());
        await File.WriteAllTextAsync(
            store.SettingsPath,
            """
            {
              "theme": "Dark",
              "sandboxModeOverride": "workspace-write",
              "approvalPolicyOverride": "on-request",
              "projectThreads": [
                {
                  "projectPath": "C:\\repo",
                  "threadId": "legacy-golden",
                  "title": "Legacy golden thread",
                  "preview": "Legacy preview",
                  "finalResponse": "Legacy response"
                }
              ]
            }
            """);

        var migrated = await store.LoadAsync();
        var permissionMigrationChanged = AppSettingsPermissionMigration.Migrate(migrated);
        await store.SaveAsync(migrated);
        var reloaded = await store.LoadAsync();
        var thread = Assert.Single(reloaded.ProjectThreads);

        AssertGolden(
            """
            {
              "theme": "Dark",
              "harnessSchemaVersion": 1,
              "executionPolicySchemaVersion": 1,
              "permissionMode": "ask-for-approval",
              "permissionMigrationChanged": true,
              "secondHarnessMigrationChanged": false,
              "secondPermissionMigrationChanged": false,
              "thread": {
                "threadId": "legacy-golden",
                "harnessId": "codex",
                "remoteConversationId": "legacy-golden",
                "conversationIdAssigned": true,
                "title": "Legacy golden thread",
                "finalResponse": "Legacy response"
              }
            }
            """,
            new
            {
                reloaded.Theme,
                reloaded.HarnessSchemaVersion,
                reloaded.ExecutionPolicySchemaVersion,
                reloaded.PermissionMode,
                PermissionMigrationChanged = permissionMigrationChanged,
                SecondHarnessMigrationChanged = AppSettingsHarnessMigration.Apply(reloaded),
                SecondPermissionMigrationChanged = AppSettingsPermissionMigration.Migrate(reloaded),
                Thread = new
                {
                    thread.ThreadId,
                    thread.HarnessId,
                    thread.RemoteConversationId,
                    ConversationIdAssigned = thread.ConversationId != Guid.Empty,
                    thread.Title,
                    thread.FinalResponse
                }
            });
    }

    private static CodexInstallation CreateInstallation() => new(
        true,
        @"C:\Tools\codex.exe",
        "codex test",
        "Codex test",
        "Test installation");

    private static async Task ConnectAsync(
        AppServerSessionCoordinator coordinator,
        FakeAppServerTransport transport,
        CodexInstallation installation)
    {
        var connectTask = coordinator.EnsureConnectedAsync(installation);
        var initialize = await WaitForRequestAsync(transport, "initialize");
        transport.ServerSend(
            $"{{\"id\":{initialize["id"]!.ToJsonString()},\"result\":{{\"userAgent\":\"golden-test\",\"platformFamily\":\"windows\",\"platformOs\":\"windows\"}}}}");
        await connectTask;
    }

    private static async Task<JsonObject> WaitForRequestAsync(
        FakeAppServerTransport transport,
        string method)
    {
        var line = await transport.ClientMessageProbe.WaitForAsync(
            candidate => string.Equals(
                JsonNode.Parse(candidate)?["method"]?.GetValue<string>(),
                method,
                StringComparison.Ordinal),
            $"{method} request");
        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"The {method} request was not a JSON object.");
    }


    private static void AssertGolden(string expectedJson, object actual)
    {
        var expected = JsonNode.Parse(expectedJson);
        var actualJson = JsonSerializer.SerializeToNode(actual, GoldenJsonOptions);
        Assert.True(
            JsonNode.DeepEquals(expected, actualJson),
            $"Golden snapshot changed.{Environment.NewLine}Expected: {expected?.ToJsonString()}{Environment.NewLine}Actual: {actualJson?.ToJsonString()}");
    }
}
