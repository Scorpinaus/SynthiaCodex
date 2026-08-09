using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using Xunit;

[Trait("Category", TestCategories.Unit)]
public sealed class ConversationWorkflowControllerTests
{
    [Fact]
    public void Removal_clears_identity_and_runtime_state()
    {
        var controller = CreateController();
        var state = CreateState("thread-a");
        controller.RegisterCreated(state);
        controller.RegisterTurnStarted(state.ThreadId, "turn-a", CodexTurnStatus.Running);

        controller.RemoveRuntime(state.ThreadId);

        Assert.False(controller.HasThread(state.ThreadId));
        Assert.False(controller.IsLoaded(state.ThreadId));
        Assert.False(controller.IsRunning(state.ThreadId));
        Assert.Equal(0, controller.ActiveTurnCount);
        Assert.Null(controller.ActiveThreadId);
    }

    [Fact]
    public void Snapshots_are_detached_from_runtime_state()
    {
        var controller = CreateController();
        var state = CreateState("detached");
        controller.RegisterCreated(state);
        controller.BeginTurn(state.ThreadId, "original");
        controller.BindPendingTurn(state.ThreadId, "turn-detached");

        var snapshot = controller.GetSnapshot(state.ThreadId);
        snapshot.ConversationTurns.Single().UserPrompt = "mutated";

        Assert.Equal("original", controller.GetSnapshot(state.ThreadId).ConversationTurns.Single().UserPrompt);
    }

    private static ConversationWorkflowController CreateController() => new(
        new ThreadStore(),
        new CodexThreadWorkspace(),
        new CodexFollowUpQueueWorkspace());

    private static ProjectThreadState CreateState(string threadId) => new()
    {
        ThreadId = threadId,
        ScopeKind = ThreadScopeKind.General,
        WorkspacePath = Path.GetTempPath()
    };
}
