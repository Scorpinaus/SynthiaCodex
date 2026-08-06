using SynthiaCode.App.Services;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.InMemory;
using Xunit;

public sealed class HarnessConversationReducerTests
{
    [Fact]
    public async Task Runtime_forwards_session_events_without_exposing_the_adapter()
    {
        await using var runtime = new HarnessRuntimeCoordinator(new HarnessRegistry([
            new InMemoryHarness()
        ]));
        var operations = new HarnessOperations(runtime);
        var observed = new TaskCompletionSource<HarnessEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.EventReceived += (_, harnessEvent) => observed.TrySetResult(harnessEvent);

        var conversationId = ConversationId.New();
        var started = await operations.StartConversationAsync(
            HarnessId.InMemory,
            new HarnessConnectionOptions(Path.GetTempPath()),
            new StartConversationCommand(
                conversationId,
                Path.GetTempPath(),
                HarnessTurnOptions.Default));

        var harnessEvent = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var conversationStarted = Assert.IsType<ConversationStartedEvent>(harnessEvent);
        Assert.Equal(started.Address.RemoteId, conversationStarted.RemoteConversationId);
    }

    [Fact]
    public void Semantic_events_route_remote_identity_to_local_state_and_reduce_the_turn()
    {
        var conversationId = ConversationId.New();
        var state = new ProjectThreadState
        {
            ThreadId = "local-conversation",
            ConversationId = conversationId.Value,
            HarnessId = KnownHarnessIds.InMemory,
            RemoteConversationId = "memory-conversation",
            ScopeKind = ThreadScopeKind.General,
            WorkspacePath = Path.GetTempPath(),
            Title = "Harness conversation"
        };
        var workspace = new CodexThreadWorkspace();
        var workflow = new ConversationWorkflowController(
            new ThreadStore(),
            workspace,
            new CodexFollowUpQueueWorkspace());
        workflow.RegisterCreated(state);
        workflow.BeginTurn(state.ThreadId, "hello");
        var timestamp = DateTimeOffset.UtcNow;

        workflow.ApplyHarnessEvent(new TurnStartedEvent(
            HarnessId.InMemory,
            "memory-conversation",
            "memory-turn",
            timestamp));
        workflow.ApplyHarnessEvent(new AssistantTextDeltaEvent(
            HarnessId.InMemory,
            "memory-conversation",
            "memory-turn",
            "message-1",
            "hello from memory",
            timestamp.AddMilliseconds(1)));
        workflow.ApplyHarnessEvent(new ActivityChangedEvent(
            HarnessId.InMemory,
            "memory-conversation",
            "memory-turn",
            new ActivityItem(
                "activity-1",
                ActivityKind.Tool,
                "Tool complete",
                "deterministic result",
                timestamp.AddMilliseconds(2),
                IsCompleted: true),
            timestamp.AddMilliseconds(2)));
        workflow.ApplyHarnessEvent(new ContextUsageChangedEvent(
            HarnessId.InMemory,
            "memory-conversation",
            25,
            100,
            timestamp.AddMilliseconds(3)));
        var completed = workflow.ApplyHarnessEvent(new TurnCompletedEvent(
            HarnessId.InMemory,
            "memory-conversation",
            "memory-turn",
            ConversationTurnStatus.Completed,
            null,
            timestamp.AddMilliseconds(4)));

        Assert.Equal(state.ThreadId, completed.ThreadId);
        Assert.True(completed.IsTurnCompleted);
        Assert.Equal(CodexTurnStatus.Completed, completed.Snapshot.ActiveTurnStatus);
        Assert.Equal("hello from memory", completed.Snapshot.FinalResponse);
        Assert.Equal(25, completed.Snapshot.ContextTokensUsed);
        Assert.Equal(100, completed.Snapshot.ContextWindowTokens);
        var turn = Assert.Single(completed.Snapshot.ConversationTurns);
        Assert.Equal("memory-turn", turn.TurnId);
        Assert.Equal("hello", turn.UserPrompt);
        Assert.Equal("hello from memory", turn.AssistantResponse);
        Assert.Contains(turn.Activity, item => item.ActivityKey == "activity-1");
        Assert.Contains(completed.Snapshot.RawEvents, item => item == nameof(TurnCompletedEvent));
    }
}
