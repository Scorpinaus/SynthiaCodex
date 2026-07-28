using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Codex;
using Xunit;

public sealed class ThreadLifecycleUseCaseServiceTests
{
    [Fact]
    public async Task Create_persists_placeholder_without_fabricating_a_conversation_turn()
    {
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new FakeSettingsStore();
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var coordinator = CreateCoordinator(transport);
        var service = CreateService(coordinator, settingsStore, store, workspace);
        var settings = new AppSettings();

        var created = await service.CreateAsync(new ThreadCreateRequest(
            settings,
            ThreadScopeKey.General,
            "created",
            "Thread 1",
            Path.GetTempPath(),
            null,
            new ThreadInstructionSnapshot(null, null),
            IsTitlePlaceholder: true));

        Assert.True(created.State.IsTitlePlaceholder);
        Assert.True(settings.ProjectThreads.Single().IsTitlePlaceholder);
        Assert.Empty(workspace.GetRequired("created").ConversationTurns);
        Assert.Equal(string.Empty, settings.ProjectThreads.Single().Preview);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Create_persistence_failure_rolls_back_durable_and_runtime_state()
    {
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new ToggleFailSettingsStore(failFromSave: 1);
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var coordinator = CreateCoordinator(transport);
        var service = CreateService(coordinator, settingsStore, store, workspace);
        var settings = new AppSettings();

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(new ThreadCreateRequest(
            settings,
            ThreadScopeKey.General,
            "cannot-create",
            "Thread 1",
            Path.GetTempPath(),
            null,
            new ThreadInstructionSnapshot(null, null),
            IsTitlePlaceholder: true)));

        Assert.Empty(settings.ProjectThreads);
        Assert.DoesNotContain("cannot-create", workspace.ThreadIds);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Delete_persistence_failure_restores_the_local_record()
    {
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new ToggleFailSettingsStore(failFromSave: 2);
        var store = new ThreadStore();
        var workspace = new CodexThreadWorkspace();
        var coordinator = CreateCoordinator(transport);
        var service = CreateService(coordinator, settingsStore, store, workspace);
        var settings = new AppSettings();
        await service.CreateAsync(new ThreadCreateRequest(
            settings,
            ThreadScopeKey.General,
            "keep-on-failure",
            "Keep",
            Path.GetTempPath(),
            null,
            new ThreadInstructionSnapshot(null, null),
            IsTitlePlaceholder: false));

        await Assert.ThrowsAsync<IOException>(() =>
            service.DeleteAsync(settings, "keep-on-failure", archiveFirst: false));

        Assert.Single(settings.ProjectThreads);
        Assert.Equal("keep-on-failure", settings.ProjectThreads.Single().ThreadId);
        Assert.False(settings.ProjectThreads.Single().IsArchived);
        await coordinator.DisposeAsync();
    }

    private static AppServerSessionCoordinator CreateCoordinator(FakeAppServerTransport transport) => new(
        new FakeCodexProcessService(transport),
        new TestLogger(),
        new CodexAppServerClientMetadata("lifecycle-use-case-tests", "Lifecycle Use Case Tests", "1.0"));

    private static ThreadLifecycleUseCaseService CreateService(
        IAppServerSessionCoordinator coordinator,
        ISettingsStore settingsStore,
        ThreadStore store,
        CodexThreadWorkspace workspace) => new(
        coordinator,
        new FakeGitService(Path.GetTempPath()),
        new FakeWorktreeService(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "lifecycle-worktree")),
        store,
        workspace,
        settingsStore);

    private sealed class ToggleFailSettingsStore(int failFromSave) : ISettingsStore
    {
        private int saves;
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "SynthiaCode.Tests", "lifecycle-failure.json");
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
