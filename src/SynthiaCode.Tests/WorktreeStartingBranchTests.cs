using System.Diagnostics;
using SynthiaCode.App.Services;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.InMemory;
using SynthiaCode.Infrastructure.Worktrees;
using Xunit;

public sealed class WorktreeStartingBranchTests
{
    [Fact]
    public async Task Current_branch_is_the_default_and_slash_names_are_preserved()
    {
        using var temp = new TemporaryDirectory();
        var repository = await CreateCommittedRepositoryAsync(temp.Path);
        await RunGitAsync(repository, "branch", "feature/with-slash");

        var catalog = await new SynthiaCode.Infrastructure.Git.GitService(new TestLogger())
            .GetBranchCatalogAsync(repository);

        Assert.Equal("main", catalog.CurrentBranch);
        Assert.Equal("main", catalog.DefaultStartPoint);
        Assert.Contains("main", catalog.Branches);
        Assert.Contains("feature/with-slash", catalog.Branches);
        Assert.True(catalog.HasHead);
    }

    [Fact]
    public async Task Detached_head_falls_back_to_HEAD()
    {
        using var temp = new TemporaryDirectory();
        var repository = await CreateCommittedRepositoryAsync(temp.Path);
        await RunGitAsync(repository, "checkout", "--detach");

        var catalog = await new SynthiaCode.Infrastructure.Git.GitService(new TestLogger())
            .GetBranchCatalogAsync(repository);

        Assert.Null(catalog.CurrentBranch);
        Assert.True(catalog.HasHead);
        Assert.Equal("HEAD", catalog.DefaultStartPoint);
    }

    [Fact]
    public async Task Selected_slash_branch_reaches_git_worktree_add_without_changing_generated_naming()
    {
        using var temp = new TemporaryDirectory();
        var repository = await CreateCommittedRepositoryAsync(temp.Path);
        await RunGitAsync(repository, "checkout", "-b", "feature/source-branch");
        await File.WriteAllTextAsync(Path.Combine(repository, "branch-only.txt"), "selected branch\n");
        await RunGitAsync(repository, "add", "branch-only.txt");
        await RunGitAsync(repository, "commit", "-m", "Selected branch content");
        var selectedHead = (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(repository, "checkout", "main");

        var service = new WorktreeService(new TestLogger());
        var worktree = await service.CreateAsync(new(
            repository,
            "Branch source",
            "thread-selected",
            "feature/source-branch"));

        Assert.Equal("branch-source", worktree.TaskId);
        Assert.Equal("codex/branch-source", worktree.Branch);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "branch-only.txt")));
        Assert.Equal(selectedHead, (await RunGitAsync(worktree.Path, "rev-parse", "HEAD")).Trim());

        await service.RemoveAsync(repository, worktree.Path);
    }

    [Fact]
    public async Task Thread_start_passes_the_selected_start_point_to_worktree_creation()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = CreateRuntime();
        var git = new FakeGitService(temp.Path)
        {
            Branches = ["main", "feature/selected"],
            CurrentBranch = "main"
        };
        var worktrees = new FakeWorktreeService(temp.Path, Path.Combine(temp.Path, "worktree"));
        var settings = new AppSettings();
        var workspace = new CodexThreadWorkspace();
        var service = CreateLifecycle(runtime, git, worktrees, new FakeSettingsStore(), workspace);

        var result = await service.StartAsync(CreateStartRequest(
            settings,
            temp.Path,
            createWorktree: true,
            startPoint: "feature/selected"));

        var request = Assert.Single(worktrees.CreateRequests);
        Assert.Equal("feature/selected", request.StartPoint);
        Assert.Equal(result.State.ThreadId, request.ThreadId);
        Assert.Equal("worktree", result.State.Mode);
        Assert.Single(settings.ProjectThreads);
    }

    [Fact]
    public async Task Thread_start_uses_HEAD_when_the_catalog_has_no_current_branch()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = CreateRuntime();
        var git = new FakeGitService(temp.Path)
        {
            Branches = ["main"],
            CurrentBranch = null,
            HasHead = true
        };
        var worktrees = new FakeWorktreeService(temp.Path, Path.Combine(temp.Path, "worktree"));
        var service = CreateLifecycle(
            runtime,
            git,
            worktrees,
            new FakeSettingsStore(),
            new CodexThreadWorkspace());

        await service.StartAsync(CreateStartRequest(
            new AppSettings(),
            temp.Path,
            createWorktree: true,
            startPoint: null));

        Assert.Equal("HEAD", Assert.Single(worktrees.CreateRequests).StartPoint);
    }

    [Fact]
    public async Task Stale_selected_branch_fails_before_thread_worktree_or_settings_mutation()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = CreateRuntime();
        var git = new FakeGitService(temp.Path) { Branches = ["main"], CurrentBranch = "main" };
        var worktrees = new FakeWorktreeService(temp.Path, Path.Combine(temp.Path, "worktree"));
        var settings = new AppSettings();
        var workspace = new CodexThreadWorkspace();
        var service = CreateLifecycle(runtime, git, worktrees, new FakeSettingsStore(), workspace);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CreateStartRequest(
            settings,
            temp.Path,
            createWorktree: true,
            startPoint: "feature/deleted")));

        Assert.Contains("no longer exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(worktrees.CreateRequests);
        Assert.Empty(settings.ProjectThreads);
        Assert.Empty(workspace.ThreadIds);
        Assert.False(runtime.TryGetSession(HarnessId.InMemory, out _));
    }

    [Fact]
    public async Task Worktree_creation_failure_archives_the_incomplete_thread_without_persisted_state()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = CreateRuntime();
        var localConversationId = ConversationId.New();
        var git = new FakeGitService(temp.Path);
        var worktrees = new FakeWorktreeService(temp.Path, Path.Combine(temp.Path, "worktree"))
        {
            CreateError = new IOException("planned worktree failure")
        };
        var settings = new AppSettings();
        var workspace = new CodexThreadWorkspace();
        var service = CreateLifecycle(runtime, git, worktrees, new FakeSettingsStore(), workspace);

        await Assert.ThrowsAsync<IOException>(() => service.StartAsync(CreateStartRequest(
            settings,
            temp.Path,
            createWorktree: true,
            startPoint: "main",
            localConversationId)));

        Assert.Empty(settings.ProjectThreads);
        Assert.Empty(workspace.ThreadIds);
        Assert.Single(worktrees.CreateRequests);
        var session = GetInMemorySession(runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartTurnAsync(new StartTurnCommand(
            new ConversationAddress(localConversationId, HarnessId.InMemory, "memory-conversation-1"),
            [new TextContentPart("must remain archived")],
            temp.Path,
            HarnessTurnOptions.Default)));
    }

    [Fact]
    public async Task Worktree_persistence_failure_removes_the_worktree_and_leaves_no_settings_entry()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = CreateRuntime();
        var localConversationId = ConversationId.New();
        var git = new FakeGitService(temp.Path);
        var worktrees = new FakeWorktreeService(temp.Path, Path.Combine(temp.Path, "worktree"));
        var settings = new AppSettings();
        var workspace = new CodexThreadWorkspace();
        var service = CreateLifecycle(runtime, git, worktrees, new AlwaysFailSettingsStore(), workspace);

        await Assert.ThrowsAsync<IOException>(() => service.StartAsync(CreateStartRequest(
            settings,
            temp.Path,
            createWorktree: true,
            startPoint: "main",
            localConversationId)));

        Assert.Empty(settings.ProjectThreads);
        Assert.Empty(workspace.ThreadIds);
        Assert.Single(worktrees.RemoveRequests);
        var session = GetInMemorySession(runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartTurnAsync(new StartTurnCommand(
            new ConversationAddress(localConversationId, HarnessId.InMemory, "memory-conversation-1"),
            [new TextContentPart("must remain archived")],
            temp.Path,
            HarnessTurnOptions.Default)));
    }

    [Fact]
    public async Task Current_checkout_project_chat_does_not_load_branches_or_create_a_worktree()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = CreateRuntime();
        var git = new FakeGitService(temp.Path);
        var worktrees = new FakeWorktreeService(temp.Path, Path.Combine(temp.Path, "unused"));
        var settings = new AppSettings();
        var workspace = new CodexThreadWorkspace();
        var service = CreateLifecycle(runtime, git, worktrees, new FakeSettingsStore(), workspace);

        var result = await service.StartAsync(CreateStartRequest(
            settings,
            temp.Path,
            createWorktree: false,
            startPoint: "ignored"));

        Assert.Equal("local", result.State.Mode);
        Assert.Equal(temp.Path, result.State.WorkspacePath);
        Assert.Null(result.Worktree);
        Assert.Equal(0, git.BranchCatalogRequestCount);
        Assert.Empty(worktrees.CreateRequests);
    }

    private static HarnessRuntimeCoordinator CreateRuntime() =>
        new(new HarnessRegistry([new InMemoryHarness()]));

    private static ThreadLifecycleUseCaseService CreateLifecycle(
        HarnessRuntimeCoordinator runtime,
        IGitService git,
        FakeWorktreeService worktrees,
        ISettingsStore settingsStore,
        CodexThreadWorkspace workspace) =>
        new(
            new HarnessOperations(runtime),
            git,
            worktrees,
            new ThreadStore(),
            workspace,
            settingsStore);

    private static ThreadStartUseCaseRequest CreateStartRequest(
        AppSettings settings,
        string workspacePath,
        bool createWorktree,
        string? startPoint,
        ConversationId? localConversationId = null) =>
        new(
            settings,
            ThreadScopeKey.ForProject(workspacePath),
            "Thread 1",
            workspacePath,
            HarnessId.InMemory,
            new HarnessConnectionOptions(workspacePath),
            new StartConversationCommand(
                localConversationId ?? ConversationId.New(),
                workspacePath,
                HarnessTurnOptions.Default),
            new ThreadInstructionSnapshot(null, null),
            IsTitlePlaceholder: true,
            CreateWorktree: createWorktree,
            WorktreeTaskId: "thread-1",
            WorktreeStartPoint: startPoint);

    private static InMemoryHarnessSession GetInMemorySession(HarnessRuntimeCoordinator runtime)
    {
        Assert.True(runtime.TryGetSession(HarnessId.InMemory, out var session));
        return Assert.IsType<InMemoryHarnessSession>(session);
    }

    private static async Task<string> CreateCommittedRepositoryAsync(string root)
    {
        var repository = Path.Combine(root, "Repo");
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.name", "SynthiaCode Tests");
        await RunGitAsync(repository, "config", "user.email", "tests@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "initial\n");
        await RunGitAsync(repository, "add", "README.md");
        await RunGitAsync(repository, "commit", "-m", "Initial commit");
        return repository;
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git.exe",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await error);
        }

        return await output;
    }

    private sealed class AlwaysFailSettingsStore : ISettingsStore
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "SynthiaCode.Tests", "worktree-start-failure.json");

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("planned settings failure"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SynthiaCode.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
