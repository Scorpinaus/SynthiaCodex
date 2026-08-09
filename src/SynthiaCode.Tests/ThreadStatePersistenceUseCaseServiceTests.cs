using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using Xunit;

[Trait("Category", TestCategories.InfrastructureIntegration)]
[Collection(TestCategories.NativeCollection)]
public sealed class ThreadStatePersistenceUseCaseServiceTests
{
    [Fact]
    public async Task Save_copies_a_detached_bounded_transcript_into_storage()
    {
        var settingsStore = new RecordingSettingsStore();
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var settings = new AppSettings();
        store.Upsert(settings, CreateState("persisted"));
        var thread = workspace.Restore(CreateState("persisted"));
        thread.BeginTurn("Persist this prompt");
        thread.BindPendingTurn("turn-persisted");
        var service = new ThreadStatePersistenceUseCaseService(settingsStore, store, workspace);

        var result = await service.SaveAsync(settings, "persisted");
        thread.ConversationTurns.Single().UserPrompt = "mutated after save";

        Assert.NotNull(result);
        Assert.Equal("Persist this prompt", settings.ProjectThreads.Single().ConversationTurns.Single().UserPrompt);
        Assert.Equal("Persist this prompt", settingsStore.SavedSettings.ProjectThreads.Single().ConversationTurns.Single().UserPrompt);
        Assert.Equal(1, settingsStore.SaveCount);
    }

    [Fact]
    public async Task SaveActive_creates_missing_durable_state_from_the_runtime_thread()
    {
        var settingsStore = new FakeSettingsStore();
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var settings = new AppSettings();
        var thread = workspace.GetOrCreate("active");
        thread.BeginTurn("First prompt");
        thread.BindPendingTurn("turn-active");
        var service = new ThreadStatePersistenceUseCaseService(settingsStore, store, workspace);

        var result = await service.SaveActiveAsync(
            settings,
            selectedThread: null,
            ThreadScopeKey.General,
            "active",
            Path.GetTempPath(),
            "Thread 1");

        Assert.Equal("active", result.State.ThreadId);
        Assert.True(result.State.IsTitlePlaceholder);
        Assert.Equal("First prompt", result.State.ConversationTurns.Single().UserPrompt);
        Assert.Single(settings.ProjectThreads);
    }

    private static ProjectThreadState CreateState(string threadId) => new()
    {
        ThreadId = threadId,
        ScopeKind = ThreadScopeKind.General,
        WorkspacePath = Path.GetTempPath(),
        Title = "Thread",
        Preview = string.Empty
    };
}
