using System.Text.Json.Nodes;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Harnesses.InMemory;
using Xunit;

public sealed class HarnessContractTests
{
    [Fact]
    public void Registry_rejects_duplicate_harness_ids()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new HarnessRegistry([new InMemoryHarness(), new InMemoryHarness()]));

        Assert.Contains(KnownHarnessIds.InMemory, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_coordinator_probes_and_reuses_one_session_per_harness()
    {
        var harness = new CountingHarness(new InMemoryHarness());
        await using var coordinator = new HarnessRuntimeCoordinator(new HarnessRegistry([harness]));

        var first = await coordinator.GetOrConnectAsync(
            HarnessId.InMemory,
            new HarnessConnectionOptions());
        var second = await coordinator.GetOrConnectAsync(
            HarnessId.InMemory,
            new HarnessConnectionOptions());

        Assert.Same(first, second);
        Assert.Equal(1, harness.ProbeCount);
        Assert.Equal(1, harness.ConnectCount);
    }

    [Fact]
    public async Task Both_adapters_expose_features_for_their_advertised_operations()
    {
        await using var memory = await new InMemoryHarness().ConnectAsync(new HarnessConnectionOptions());
        var backend = new FakeCodexBackend();
        var codexHarness = new CodexHarness(
            new StubCodexDiscovery(),
            backend);
        await using var codex = await codexHarness.ConnectAsync(new HarnessConnectionOptions());

        AssertOperationalFeatureContract(memory);
        AssertOperationalFeatureContract(codex);
    }

    [Fact]
    public async Task In_memory_harness_creates_streams_completes_restores_and_cancels()
    {
        await using var session = await new InMemoryHarness().ConnectAsync(new HarnessConnectionOptions());
        var observed = new List<HarnessEvent>();
        session.EventReceived += (_, harnessEvent) => observed.Add(harnessEvent);

        var creation = session.RequireFeature<IConversationCreationFeature>(HarnessCapability.CreateConversation);
        var execution = session.RequireFeature<ITurnExecutionFeature>(HarnessCapability.StartTurn);
        var read = session.RequireFeature<IConversationReadFeature>(HarnessCapability.ReadConversation);
        var resume = session.RequireFeature<IConversationResumeFeature>(HarnessCapability.ResumeConversation);
        var cancellation = session.RequireFeature<ITurnCancellationFeature>(HarnessCapability.CancelTurn);
        var address = (await creation.StartConversationAsync(new StartConversationCommand(
            ConversationId.New(),
            "C:\\workspace",
            HarnessTurnOptions.Default))).Address;

        var started = await execution.StartTurnAsync(new StartTurnCommand(
            address,
            [new TextContentPart("hello")],
            "C:\\workspace",
            HarnessTurnOptions.Default));
        var inMemory = Assert.IsType<InMemoryHarnessSession>(session);
        inMemory.EmitAssistantText(address, started.RemoteTurnId, "hello ");
        inMemory.EmitAssistantText(address, started.RemoteTurnId, "world");
        inMemory.CompleteTurn(address, started.RemoteTurnId);

        var readResult = await read.ReadConversationAsync(new ReadConversationCommand(address));
        var turn = Assert.Single(readResult.Turns);
        Assert.Equal("hello", turn.UserPrompt);
        Assert.Equal("hello world", turn.AssistantResponse);
        Assert.Equal(ConversationTurnStatus.Completed, turn.Status);

        var resumed = await resume.ResumeConversationAsync(new ResumeConversationCommand(
            address,
            "C:\\workspace",
            HarnessTurnOptions.Default));
        Assert.Single(resumed.Turns);

        var cancellable = await execution.StartTurnAsync(new StartTurnCommand(
            address,
            [new TextContentPart("cancel me")],
            "C:\\workspace",
            HarnessTurnOptions.Default));
        await cancellation.CancelTurnAsync(address, cancellable.RemoteTurnId);

        Assert.Contains(observed, item => item is ConversationStartedEvent);
        Assert.Equal(2, observed.OfType<TurnStartedEvent>().Count());
        Assert.Equal("hello world", string.Concat(observed.OfType<AssistantTextDeltaEvent>().Select(item => item.Delta)));
        Assert.Contains(observed, item => item is TurnCompletedEvent
        {
            RemoteTurnId: var turnId,
            Status: ConversationTurnStatus.Cancelled
        } && turnId == cancellable.RemoteTurnId);
    }

    [Fact]
    public async Task Codex_adapter_maps_neutral_commands_and_translates_stream_events()
    {
        var backend = new FakeCodexBackend();
        var harness = new CodexHarness(new StubCodexDiscovery(), backend);
        await using var session = await harness.ConnectAsync(new HarnessConnectionOptions());
        var observed = new List<HarnessEvent>();
        session.EventReceived += (_, harnessEvent) => observed.Add(harnessEvent);

        var creation = session.RequireFeature<IConversationCreationFeature>(HarnessCapability.CreateConversation);
        var execution = session.RequireFeature<ITurnExecutionFeature>(HarnessCapability.StartTurn);
        var address = (await creation.StartConversationAsync(new StartConversationCommand(
            ConversationId.New(),
            "C:\\workspace",
            new HarnessTurnOptions(
                "gpt-test",
                "high",
                "fast",
                new HarnessExecutionPolicy(
                    WorkspaceAccessMode.WorkspaceWrite,
                    ApprovalInteractionMode.Prompt))))).Address;
        await execution.StartTurnAsync(new StartTurnCommand(
            address,
            [new TextContentPart("hello"), new LocalImageContentPart("C:\\workspace\\image.png")],
            "C:\\workspace",
            new HarnessTurnOptions("gpt-test", "high", "fast")));

        Assert.Equal("gpt-test", backend.LastThreadStart?.Model);
        Assert.Equal(CodexSandbox.WorkspaceWrite, backend.LastThreadStart?.Sandbox);
        Assert.Equal(CodexApprovalPolicy.OnRequest, backend.LastThreadStart?.ApprovalPolicy);
        Assert.Equal(CodexReasoningEffort.High, backend.LastTurnStart?.ReasoningEffort);
        Assert.Equal(CodexServiceTierSelection.Fast, backend.LastTurnStart?.ServiceTier);
        Assert.Collection(
            backend.LastTurnStart!.Inputs,
            item => Assert.IsType<CodexTextInput>(item),
            item => Assert.IsType<CodexLocalImageInput>(item));

        backend.Raise(CodexAppServerNotification.Decode(new AppServerNotification(
            CodexAppServerNotificationMethods.AgentMessageDelta,
            new JsonObject
            {
                ["threadId"] = "codex-thread",
                ["turnId"] = "codex-turn",
                ["itemId"] = "message-1",
                ["delta"] = "streamed"
            })));

        var delta = Assert.Single(observed.OfType<AssistantTextDeltaEvent>());
        Assert.Equal("streamed", delta.Delta);
        Assert.Equal("codex-thread", delta.RemoteConversationId);
    }

    [Fact]
    public void Codex_translator_preserves_authoritative_agent_messages_and_phases()
    {
        var timestamp = DateTimeOffset.UtcNow;

        var finalOnly = Assert.Single(CodexHarnessEventTranslator.Translate(Notification(
            CodexAppServerNotificationMethods.ItemCompleted,
            "final-only",
            "Final only",
            "final_answer"),
            timestamp));
        var finalOnlyMessage = Assert.IsType<AssistantMessageCompletedEvent>(finalOnly);
        Assert.Equal("final-only", finalOnlyMessage.MessageId);
        Assert.Equal("Final only", finalOnlyMessage.Text);
        Assert.Equal("final_answer", finalOnlyMessage.Phase);

        var streamed = Assert.Single(CodexHarnessEventTranslator.Translate(
            CodexAppServerNotification.Decode(new AppServerNotification(
                CodexAppServerNotificationMethods.AgentMessageDelta,
                new JsonObject
                {
                    ["threadId"] = "codex-thread",
                    ["turnId"] = "codex-turn",
                    ["itemId"] = "streamed",
                    ["delta"] = "Streamed draft"
                })),
            timestamp.AddMilliseconds(1)));
        var streamedDelta = Assert.IsType<AssistantTextDeltaEvent>(streamed);
        var streamedCompletion = Assert.Single(CodexHarnessEventTranslator.Translate(Notification(
            CodexAppServerNotificationMethods.ItemCompleted,
            "streamed",
            "Authoritative final",
            "final_answer"),
            timestamp.AddMilliseconds(2)));
        var completedStream = Assert.IsType<AssistantMessageCompletedEvent>(streamedCompletion);
        Assert.Equal(streamedDelta.MessageId, completedStream.MessageId);
        Assert.Equal("Authoritative final", completedStream.Text);

        var commentary = Assert.Single(CodexHarnessEventTranslator.Translate(Notification(
            CodexAppServerNotificationMethods.ItemCompleted,
            "commentary",
            "Working through it",
            "commentary"),
            timestamp.AddMilliseconds(3)));
        var commentaryMessage = Assert.IsType<AssistantMessageCompletedEvent>(commentary);
        Assert.Equal("commentary", commentaryMessage.Phase);
        Assert.Equal("Working through it", commentaryMessage.Text);

        static CodexAppServerNotification Notification(
            string method,
            string itemId,
            string text,
            string phase) => CodexAppServerNotification.Decode(new AppServerNotification(
                method,
                new JsonObject
                {
                    ["threadId"] = "codex-thread",
                    ["turnId"] = "codex-turn",
                    ["item"] = new JsonObject
                    {
                        ["type"] = "agentMessage",
                        ["id"] = itemId,
                        ["text"] = text,
                        ["phase"] = phase
                    }
                }));
    }

    private static void AssertOperationalFeatureContract(IHarnessSession session)
    {
        AssertFeature<IConversationCreationFeature>(session, HarnessCapability.CreateConversation);
        AssertFeature<IConversationResumeFeature>(session, HarnessCapability.ResumeConversation);
        AssertFeature<IConversationReadFeature>(session, HarnessCapability.ReadConversation);
        AssertFeature<IConversationNamingFeature>(session, HarnessCapability.RenameConversation);
        AssertFeature<IConversationArchiveFeature>(session, HarnessCapability.ArchiveConversation);
        AssertFeature<IConversationForkFeature>(session, HarnessCapability.ForkConversation);
        AssertFeature<IConversationRollbackFeature>(session, HarnessCapability.RollbackConversation);
        AssertFeature<ITurnExecutionFeature>(session, HarnessCapability.StartTurn);
        AssertFeature<ITurnCancellationFeature>(session, HarnessCapability.CancelTurn);
        AssertFeature<ITurnSteeringFeature>(session, HarnessCapability.SteerTurn);
        AssertFeature<IModelCatalogFeature>(session, HarnessCapability.ModelCatalog);
    }

    private static void AssertFeature<TFeature>(IHarnessSession session, HarnessCapability capability)
        where TFeature : class, IHarnessFeature
    {
        Assert.True(session.Capabilities.Supports(capability));
        Assert.True(session.TryGetFeature<TFeature>(out var feature));
        Assert.NotNull(feature);
    }

    private sealed class CountingHarness(IAgentHarness inner) : IAgentHarness
    {
        public int ProbeCount { get; private set; }
        public int ConnectCount { get; private set; }
        public HarnessDescriptor Descriptor => inner.Descriptor;

        public async Task<HarnessAvailability> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            return await inner.ProbeAsync(cancellationToken);
        }

        public async Task<IHarnessSession> ConnectAsync(
            HarnessConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            return await inner.ConnectAsync(options, cancellationToken);
        }
    }

    private sealed class StubCodexDiscovery : ICodexDiscoveryService
    {
        public Task<CodexInstallation> DetectAsync(
            string? preferredExecutablePath = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new CodexInstallation(
                true,
                preferredExecutablePath ?? "codex.exe",
                "test",
                "Codex test installation",
                "Available for adapter tests."));
    }

    private sealed class FakeCodexBackend : ICodexHarnessBackend
    {
        public event EventHandler<CodexAppServerNotification>? NotificationReceived;

        public CodexThreadStartOptions? LastThreadStart { get; private set; }
        public CodexTurnStartRequest? LastTurnStart { get; private set; }

        public void Raise(CodexAppServerNotification notification) =>
            NotificationReceived?.Invoke(this, notification);

        public Task EnsureConnectedAsync(CodexInstallation installation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CodexThreadStartResult> StartThreadAsync(
            CodexThreadStartOptions options,
            CancellationToken cancellationToken = default)
        {
            LastThreadStart = options;
            return Task.FromResult(new CodexThreadStartResult("codex-thread"));
        }

        public Task<CodexThreadResumeResult> ResumeThreadAsync(
            CodexThreadResumeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexThreadResumeResult(request.ThreadId, []));

        public Task<CodexThreadReadResult> ReadThreadAsync(
            CodexThreadReadRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexThreadReadResult(request.ThreadId, []));

        public Task<CodexThreadForkResult> ForkThreadAsync(
            CodexThreadForkRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexThreadForkResult("codex-fork"));

        public Task<CodexThreadRollbackResult> RollbackThreadAsync(
            CodexThreadRollbackRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexThreadRollbackResult(request.ThreadId, []));

        public Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetThreadNameAsync(
            string threadId,
            string name,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CodexTurnStartResult> StartTurnAsync(
            CodexTurnStartRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTurnStart = request;
            return Task.FromResult(new CodexTurnStartResult("codex-turn"));
        }

        public Task<CodexTurnSteerResult> SteerTurnAsync(
            CodexTurnSteerRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexTurnSteerResult(request.ExpectedTurnId));

        public Task CancelTurnAsync(
            string threadId,
            string turnId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CodexModelOption>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodexModelOption>>([]);
    }
}
