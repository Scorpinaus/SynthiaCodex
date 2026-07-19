using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Workspaces;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

internal static class ProjectlessThreadTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("projectless thread store isolates General scope", ThreadStoreIsolatesGeneralScopeAsync),
        ("General workspace is contained and idempotent", GeneralWorkspaceIsContainedAndIdempotentAsync),
        ("thread start serializes an optional cwd", ThreadStartSerializesOptionalCwdAsync),
        ("view model creates a General thread without a project", ViewModelCreatesGeneralThreadWithoutProjectAsync),
        ("view model submits a first General turn without a project", ViewModelSubmitsFirstGeneralTurnWithoutProjectAsync),
        ("General workspace failure leaves project thread creation available", GeneralWorkspaceFailureIsIsolatedAsync),
        ("General threads support fork archive unarchive and resume", GeneralThreadLifecycleAsync)
    ];

    private static Task ThreadStoreIsolatesGeneralScopeAsync()
    {
        using var temp = TempWorkspace.Create();
        var projectPath = temp.CreateDirectory("Repo");
        var generalPath = temp.CreateDirectory("General");
        var settings = new AppSettings();
        var store = new ThreadStore();

        store.Upsert(settings, new ProjectThreadState
        {
            ScopeKind = ThreadScopeKind.Project,
            ProjectPath = projectPath,
            WorkspacePath = projectPath,
            ThreadId = "project-thread"
        });
        store.Upsert(settings, new ProjectThreadState
        {
            ScopeKind = ThreadScopeKind.General,
            ProjectPath = string.Empty,
            WorkspacePath = generalPath,
            ThreadId = "general-thread"
        });
        store.SetActive(settings, ThreadScopeKey.General, "general-thread");

        Assert(store.GetThreads(settings, ThreadScopeKey.General).Single().ThreadId == "general-thread", "General query returns only General thread");
        Assert(store.GetProjectThreads(settings, projectPath).Single().ThreadId == "project-thread", "project query excludes General thread");
        Assert(store.GetActive(settings, ThreadScopeKey.General)?.ThreadId == "general-thread", "General active thread is retained");
        Assert(settings.ProjectThreads.Single(thread => thread.ThreadId == "project-thread").ScopeKind == ThreadScopeKind.Project, "project scope persists");
        Assert(settings.ProjectThreads.Single(thread => thread.ThreadId == "general-thread").ScopeKind == ThreadScopeKind.General, "General scope persists");
        return Task.CompletedTask;
    }

    private static Task GeneralWorkspaceIsContainedAndIdempotentAsync()
    {
        using var temp = TempWorkspace.Create();
        var service = new GeneralWorkspaceService(temp.Root);

        var first = service.EnsureWorkspace();
        var second = service.EnsureWorkspace();
        var expected = Path.GetFullPath(Path.Combine(temp.Root, "workspaces", "general"));

        Assert(first == expected, "General workspace uses the app-data workspaces root");
        Assert(second == first, "General workspace resolution is idempotent");
        Assert(Directory.Exists(first), "General workspace is created");
        Assert(Path.GetRelativePath(temp.Root, first).Split(Path.DirectorySeparatorChar)[0] != "..", "General workspace remains contained");
        return Task.CompletedTask;
    }

    private static async Task ThreadStartSerializesOptionalCwdAsync()
    {
        using var temp = TempWorkspace.Create();
        var cwd = temp.CreateDirectory("General");
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("projectless_tests", "Projectless Tests", "1.0.0"));
        await CompleteInitializeAsync(client, transport);

        var startTask = client.StartThreadAsync(new CodexThreadStartOptions(Cwd: cwd));
        await transport.WaitForClientMessageCountAsync(3);
        var request = ParseMessage(transport.ClientMessages[2]);
        Assert(ReadString(request, "method") == "thread/start", "thread start method");
        Assert(ReadString(request, "params.cwd") == cwd, "thread start cwd");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"general-thread"}}}""");
        Assert((await startTask).ThreadId == "general-thread", "thread start response");
    }

    private static async Task ViewModelCreatesGeneralThreadWithoutProjectAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new FakeSettingsStore();
        var workspaceService = new GeneralWorkspaceService(temp.Root);
        var viewModel = CreateViewModel(transport, temp.Root, settingsStore, workspaceService);
        await viewModel.InitializeAsync();

        Assert(viewModel.SelectedProjectPath is null, "no project is selected");
        Assert(viewModel.ActiveWorkspacePath == workspaceService.WorkspacePath, "General workspace is presented before a thread exists");
        Assert(viewModel.NewThreadCommand.CanExecute(null), "General thread command is enabled");
        viewModel.NewThreadCommand.Execute(null);

        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        var start = ParseMessage(transport.ClientMessages[2]);
        Assert(ReadString(start, "method") == "thread/start", "General creation starts a thread");
        Assert(ReadString(start, "params.cwd") == workspaceService.WorkspacePath, "General creation uses managed cwd");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"general-created"}}}""");

        await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "general-created", "General thread selected");
        Assert(viewModel.SelectedThread?.ScopeKind == ThreadScopeKind.General, "selected thread is General");
        Assert(viewModel.ProjectWorkspace.GeneralThreads.Single().ThreadId == "general-created", "General navigation contains thread");
        Assert(settingsStore.SavedSettings.ProjectThreads.Single().ScopeKind == ThreadScopeKind.General, "General thread persisted");
        await viewModel.DisposeAsync();
    }

    private static async Task ViewModelSubmitsFirstGeneralTurnWithoutProjectAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var workspaceService = new GeneralWorkspaceService(temp.Root);
        var viewModel = CreateViewModel(transport, temp.Root, new FakeSettingsStore(), workspaceService);
        await viewModel.InitializeAsync();
        viewModel.PromptText = "Explain the General workspace.";
        viewModel.SubmitPromptCommand.Execute(null);

        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        Assert(ReadString(ParseMessage(transport.ClientMessages[2]), "method") == "thread/start", "implicit General thread starts");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"general-implicit"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        var turn = ParseMessage(transport.ClientMessages[3]);
        Assert(ReadString(turn, "method") == "turn/start", "implicit General turn starts");
        Assert(ReadString(turn, "params.cwd") == workspaceService.WorkspacePath, "implicit General turn uses managed cwd");
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-general"}}}""");
        await WaitUntilAsync(() => viewModel.IsTurnRunning, "General turn running");
        await viewModel.DisposeAsync();
    }

    private static async Task GeneralThreadLifecycleAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var workspaceService = new GeneralWorkspaceService(temp.Root);
        var viewModel = CreateViewModel(transport, temp.Root, new FakeSettingsStore(), workspaceService);
        await viewModel.InitializeAsync();
        viewModel.NewThreadCommand.Execute(null);

        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"general-source"}}}""");
        await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "general-source", "General source selected");

        viewModel.ForkThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(4);
        var fork = ParseMessage(transport.ClientMessages[3]);
        Assert(ReadString(fork, "method") == "thread/fork", "General fork request method");
        Assert(ReadString(fork, "params.cwd") == workspaceService.WorkspacePath, "General fork uses managed cwd");
        transport.ServerSend("""{"id":2,"result":{"thread":{"id":"general-fork"}}}""");
        await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "general-fork", "General fork selected");
        Assert(viewModel.SelectedThread?.ScopeKind == ThreadScopeKind.General, "fork remains in General scope");

        viewModel.ArchiveThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(5);
        transport.ServerSend("""{"id":3,"result":{}}""");
        await WaitUntilAsync(() => viewModel.SelectedThread?.IsArchived == true, "General fork archived");

        viewModel.UnarchiveThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);
        transport.ServerSend("""{"id":4,"result":{"thread":{"id":"general-fork"}}}""");
        await WaitUntilAsync(() => viewModel.SelectedThread?.IsArchived == false, "General fork unarchived");

        viewModel.ResumeThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(7);
        var resume = ParseMessage(transport.ClientMessages[6]);
        Assert(ReadString(resume, "method") == "thread/resume", "General resume request method");
        Assert(ReadString(resume, "params.cwd") == workspaceService.WorkspacePath, "General resume uses managed cwd");
        transport.ServerSend("""{"id":5,"result":{"thread":{"id":"general-fork"},"turns":[]}}""");
        await WaitUntilAsync(() => viewModel.StatusMessage.Contains("resumed", StringComparison.OrdinalIgnoreCase), "General fork resumed");
        await viewModel.DisposeAsync();
    }

    private static async Task GeneralWorkspaceFailureIsIsolatedAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var viewModel = CreateViewModel(
            transport,
            temp.Root,
            new FakeSettingsStore(),
            new UnavailableGeneralWorkspaceService(Path.Combine(temp.Root, "blocked-general")));
        await viewModel.InitializeAsync();

        Assert(!viewModel.ProjectWorkspace.NewGeneralThreadCommand.CanExecute(null), "unavailable General creation is disabled");
        Assert(!viewModel.NewThreadCommand.CanExecute(null), "global creation is disabled when no current scope is available");
        viewModel.BrowseProjectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "project selected after General failure");
        Assert(viewModel.NewThreadCommand.CanExecute(null), "project thread creation remains available");
        await viewModel.DisposeAsync();
    }

    private static MainViewModel CreateViewModel(
        FakeAppServerTransport transport,
        string root,
        FakeSettingsStore settingsStore,
        IGeneralWorkspaceService workspaceService)
    {
        var logger = new TestLogger();
        var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("projectless_tests", "Projectless Tests", "1.0.0"));
        return new MainViewModel(
            settingsStore,
            new FakeCodexDiscoveryService(installation),
            coordinator,
            new FakeAuthService(new AuthenticationState(AuthReadiness.LikelySignedIn, "Ready", "Test auth", @"C:\Users\Test\.codex")),
            new FakeGitService(root),
            new FakeWorktreeService(root, Path.Combine(root, ".test-worktree")),
            new RecentProjectService(),
            new FakeFolderPicker(root),
            new FakeUserInteractionService(),
            new FakeThemeService(),
            new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new CodexThreadWorkspace(),
            new FakeTerminalService(),
            logger,
            generalWorkspaceService: workspaceService);
    }

    private static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await initialize;
    }

    private static JsonObject ParseMessage(string line) =>
        JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException($"Message was not an object: {line}");

    private static string? ReadString(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
        }
        return current?.GetValue<string>();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"Timed out waiting for {label}.");
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class UnavailableGeneralWorkspaceService(string workspacePath) : IGeneralWorkspaceService
    {
        public string WorkspacePath { get; } = workspacePath;

        public string EnsureWorkspace() => throw new IOException("simulated General workspace failure");
    }
}
