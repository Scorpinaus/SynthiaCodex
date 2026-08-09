using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.InMemory;
using Xunit;

[Trait("Category", TestCategories.Unit)]
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

    [Fact]
    public void Completed_agent_message_supplies_a_final_only_response()
    {
        var (service, timestamp) = CreateRunningTurn();

        service.ApplyEvent(new AssistantMessageCompletedEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            "final-only",
            "Final only response",
            "final_answer",
            timestamp.AddMilliseconds(1)));
        CompleteTurn(service, timestamp.AddMilliseconds(2));

        var turn = Assert.Single(service.ConversationTurns);
        Assert.Equal("Final only response", turn.AssistantResponse);
        Assert.Equal("Final only response", service.FinalResponse);
    }

    [Fact]
    public void Completed_agent_message_replaces_streamed_text_without_duplication()
    {
        var (service, timestamp) = CreateRunningTurn();

        service.ApplyEvent(new AssistantTextDeltaEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            "streamed",
            "Streamed draft",
            timestamp.AddMilliseconds(1)));
        service.ApplyEvent(new AssistantMessageCompletedEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            "streamed",
            "Authoritative final",
            "final_answer",
            timestamp.AddMilliseconds(2)));
        CompleteTurn(service, timestamp.AddMilliseconds(3));

        var turn = Assert.Single(service.ConversationTurns);
        Assert.Equal("Authoritative final", turn.AssistantResponse);
        Assert.Equal("Authoritative final", service.FinalResponse);
    }

    [Fact]
    public void Commentary_agent_message_is_activity_not_final_response()
    {
        var (service, timestamp) = CreateRunningTurn();

        service.ApplyEvent(new AssistantTextDeltaEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            "commentary",
            "Working",
            timestamp.AddMilliseconds(1)));
        service.ApplyEvent(new AssistantMessageCompletedEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            "commentary",
            "Working through it",
            "commentary",
            timestamp.AddMilliseconds(2)));
        Assert.Empty(service.FinalResponse);

        service.ApplyEvent(new AssistantMessageCompletedEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            "final",
            "Visible answer",
            "final_answer",
            timestamp.AddMilliseconds(3)));
        CompleteTurn(service, timestamp.AddMilliseconds(4));

        var turn = Assert.Single(service.ConversationTurns);
        Assert.Equal("Visible answer", turn.AssistantResponse);
        var commentary = Assert.Single(
            turn.Activity,
            item => item.Kind == CodexTimelineItemKind.AssistantCommentary);
        Assert.Contains("Working through it", commentary.Detail, StringComparison.Ordinal);
    }

    private static (CodexThreadService Service, DateTimeOffset Timestamp) CreateRunningTurn()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var service = new CodexThreadService();
        service.Restore("codex-conversation", null, null, null);
        service.BeginTurn("hello");
        service.ApplyEvent(new TurnStartedEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            timestamp));
        return (service, timestamp);
    }

    private static void CompleteTurn(CodexThreadService service, DateTimeOffset timestamp) =>
        service.ApplyEvent(new TurnCompletedEvent(
            HarnessId.Codex,
            "codex-conversation",
            "codex-turn",
            ConversationTurnStatus.Completed,
            null,
            timestamp));
}
