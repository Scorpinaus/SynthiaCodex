using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.InMemory;
using Xunit;

public sealed class Phase1ConversationSliceTests
{
    [Fact]
    public void Conversation_slice_is_owned_by_application_while_codex_side_features_remain_in_app()
    {
        var applicationAssembly = typeof(IConversationFeatureFacade).Assembly;
        var appAssembly = typeof(MainViewModel).Assembly;
        var movedTypes = new[]
        {
            typeof(ConversationWorkflowController),
            typeof(ThreadLifecycleUseCaseService),
            typeof(ThreadStatePersistenceUseCaseService),
            typeof(TurnExecutionUseCaseService),
            typeof(FollowUpQueueUseCaseService),
        };

        Assert.All(movedTypes, type => Assert.Same(applicationAssembly, type.Assembly));
        Assert.Null(appAssembly.GetType("SynthiaCode.App.Services.ConversationWorkflowController"));
        Assert.Null(appAssembly.GetType("SynthiaCode.App.Services.TurnExecutionUseCaseService"));
        Assert.Null(appAssembly.GetType("SynthiaCode.App.Services.ThreadLifecycleUseCaseService"));
        Assert.Null(appAssembly.GetType("SynthiaCode.App.Services.ThreadStatePersistenceUseCaseService"));
        Assert.Null(appAssembly.GetType("SynthiaCode.App.Services.FollowUpQueueUseCaseService"));

        Assert.Same(appAssembly, typeof(CodeReviewUseCaseService).Assembly);
        Assert.Same(appAssembly, typeof(SkillsViewModel).Assembly);
    }

    [Fact]
    public void Main_view_model_receives_one_facade_and_use_case_requests_have_no_ui_delegates()
    {
        var constructor = Assert.Single(typeof(MainViewModel).GetConstructors());
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Assert.Single(parameterTypes, type => type == typeof(IConversationFeatureFacade));
        Assert.DoesNotContain(typeof(ConversationWorkflowController), parameterTypes);
        Assert.DoesNotContain(typeof(ThreadLifecycleUseCaseService), parameterTypes);
        Assert.DoesNotContain(typeof(ThreadStatePersistenceUseCaseService), parameterTypes);
        Assert.DoesNotContain(typeof(TurnExecutionUseCaseService), parameterTypes);
        Assert.DoesNotContain(typeof(FollowUpQueueUseCaseService), parameterTypes);

        AssertRequestHasNoDelegates(typeof(TurnExecutionRequest));
        AssertRequestHasNoDelegates(typeof(FollowUpDispatchUseCaseRequest));
        AssertRequestHasNoDelegates(typeof(CodeReviewExecutionRequest));
    }

    [Fact]
    public async Task Facade_forwards_workspace_events_as_detached_application_state()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var settingsStore = new FakeSettingsStore();
        var threadStore = new ThreadStore();
        var threadWorkspace = new CodexThreadWorkspace();
        var followUpQueues = new CodexFollowUpQueueWorkspace();
        await using var runtime = new HarnessRuntimeCoordinator(new HarnessRegistry([new InMemoryHarness()]));
        await using var facade = new ConversationFeatureFacade(
            new HarnessOperations(runtime),
            new FakeGitService(root),
            new FakeWorktreeService(root, Path.Combine(root, "phase1-worktree")),
            settingsStore,
            threadStore,
            threadWorkspace,
            followUpQueues);
        var state = new ProjectThreadState
        {
            ThreadId = "phase1-event-thread",
            ScopeKind = ThreadScopeKind.General,
            WorkspacePath = root,
            Title = "Phase 1 event"
        };
        threadStore.Upsert(settingsStore.SavedSettings, state);
        facade.Workspace.RegisterCreated(state);
        ConversationWorkspaceChangedEvent? observed = null;
        facade.Changed += (_, change) => observed = change;

        facade.Workspace.BeginTurn(state.ThreadId, "Move the feature slice.");

        Assert.NotNull(observed);
        Assert.Equal(ConversationWorkspaceChangeKind.PendingTurnStarted, observed.Kind);
        Assert.Equal(state.ThreadId, observed.ThreadId);
        Assert.Equal("Move the feature slice.", Assert.Single(observed.Snapshot.ConversationTurns).UserPrompt);
    }

    [Fact]
    public void Completed_turn_cannot_be_reopened_by_a_late_start_continuation()
    {
        var state = new ProjectThreadState
        {
            ThreadId = "phase1-ordered-thread",
            ConversationId = ConversationId.New().Value,
            HarnessId = KnownHarnessIds.InMemory,
            RemoteConversationId = "phase1-ordered-remote",
            ScopeKind = ThreadScopeKind.General,
            WorkspacePath = Path.GetTempPath(),
            Title = "Ordered lifecycle"
        };
        var workspace = new ConversationWorkflowController(
            new ThreadStore(),
            new CodexThreadWorkspace(),
            new CodexFollowUpQueueWorkspace());
        workspace.RegisterCreated(state);
        workspace.BeginTurn(state.ThreadId, "Complete immediately.");
        var bound = workspace.BindPendingTurn(state.ThreadId, "phase1-turn");

        workspace.ApplyHarnessEvent(new TurnCompletedEvent(
            HarnessId.InMemory,
            state.RemoteConversationId,
            "phase1-turn",
            ConversationTurnStatus.Completed,
            null,
            DateTimeOffset.UtcNow));
        workspace.RegisterTurnStarted(state.ThreadId, "phase1-turn", bound.Status);

        Assert.False(workspace.IsRunning(state.ThreadId));
        Assert.False(workspace.TryGetActiveTurn(state.ThreadId, out _));
        Assert.Equal(CodexTurnStatus.Completed, workspace.GetSnapshot(state.ThreadId).ActiveTurnStatus);
    }

    private static void AssertRequestHasNoDelegates(Type requestType)
    {
        Assert.DoesNotContain(
            requestType.GetProperties(),
            property => typeof(Delegate).IsAssignableFrom(property.PropertyType));
    }
}
