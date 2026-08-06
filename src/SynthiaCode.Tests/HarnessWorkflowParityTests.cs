using SynthiaCode.App.Services;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.InMemory;
using Xunit;

public sealed class HarnessWorkflowParityTests
{
    [Fact]
    public async Task In_memory_harness_drives_the_same_lifecycle_turn_and_persistence_use_cases()
    {
        await using var runtime = new HarnessRuntimeCoordinator(new HarnessRegistry([
            new InMemoryHarness()
        ]));
        var harnesses = new HarnessOperations(runtime);
        var settingsStore = new FakeSettingsStore();
        var threadStore = new ThreadStore();
        var threadWorkspace = new CodexThreadWorkspace();
        var queueWorkspace = new CodexFollowUpQueueWorkspace();
        var conversations = new ConversationWorkflowController(
            threadStore,
            threadWorkspace,
            queueWorkspace);
        runtime.EventReceived += (_, harnessEvent) => conversations.ApplyHarnessEvent(harnessEvent);
        var lifecycle = new ThreadLifecycleUseCaseService(
            harnesses,
            new FakeGitService(Path.GetTempPath()),
            new FakeWorktreeService(
                Path.GetTempPath(),
                Path.Combine(Path.GetTempPath(), "memory-harness-worktree")),
            threadStore,
            threadWorkspace,
            settingsStore);
        var persistence = new ThreadStatePersistenceUseCaseService(
            settingsStore,
            threadStore,
            threadWorkspace);
        var execution = new TurnExecutionUseCaseService(
            harnesses,
            conversations,
            lifecycle,
            persistence);
        var settings = new AppSettings { DefaultHarnessId = KnownHarnessIds.InMemory };
        var connection = new HarnessConnectionOptions(Path.GetTempPath());

        var startedConversation = await lifecycle.StartAsync(new ThreadStartUseCaseRequest(
            settings,
            ThreadScopeKey.General,
            "New conversation",
            Path.GetTempPath(),
            HarnessId.InMemory,
            connection,
            new StartConversationCommand(
                ConversationId.New(),
                Path.GetTempPath(),
                HarnessTurnOptions.Default),
            new ThreadInstructionSnapshot(null, null),
            IsTitlePlaceholder: true,
            CreateWorktree: false,
            WorktreeTaskId: string.Empty));
        conversations.MarkLoaded(startedConversation.State.ThreadId);
        var address = startedConversation.State.GetConversationAddress();

        var startedTurn = await execution.StartAsync(new TurnExecutionRequest(
            settings,
            startedConversation.State.ThreadId,
            address,
            "hello",
            [],
            connection,
            new StartTurnCommand(
                address,
                [new TextContentPart("hello")],
                Path.GetTempPath(),
                HarnessTurnOptions.Default),
            "Memory title"));
        Assert.True(runtime.TryGetSession(HarnessId.InMemory, out var session));
        var inMemory = Assert.IsType<InMemoryHarnessSession>(session);
        inMemory.EmitAssistantText(address, startedTurn.TurnId, "portable response");
        inMemory.CompleteTurn(address, startedTurn.TurnId);
        var persisted = await persistence.SaveAsync(
            settings,
            startedConversation.State.ThreadId);

        var snapshot = conversations.GetSnapshot(startedConversation.State.ThreadId);
        Assert.Equal(CodexTurnStatus.Completed, snapshot.ActiveTurnStatus);
        Assert.Equal("portable response", snapshot.FinalResponse);
        Assert.True(startedTurn.AutomaticTitleApplied);
        Assert.Equal("Memory title", settings.ProjectThreads.Single().Title);
        Assert.Equal(KnownHarnessIds.InMemory, settings.ProjectThreads.Single().HarnessId);
        Assert.Equal(address.LocalId.Value, settings.ProjectThreads.Single().ConversationId);
        Assert.Equal(address.RemoteId, settings.ProjectThreads.Single().RemoteConversationId);
        Assert.NotNull(persisted);
        Assert.Equal("portable response", persisted!.State.FinalResponse);
    }
}
