using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

internal static class ThreadRenameTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("app-server client sends thread rename requests", AppServerClientSendsThreadRenameRequestsAsync),
        ("thread store renames chats and updates presentation titles", ThreadStoreRenamesChatsAsync),
        ("sidebar rename command supports general and project chats", SidebarRenameCommandSupportsBothScopesAsync),
        ("general and project chat menus expose rename", ChatMenusExposeRenameAsync),
        ("main view model renames and persists general and project chats", MainViewModelRenamesBothScopesAsync),
        ("first message automatically renames a new chat once", FirstMessageAutomaticallyRenamesNewChatOnceAsync)
    ];

    private static async Task AppServerClientSendsThreadRenameRequestsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("thread_rename_tests", "Thread Rename Tests", "1.0.0"));
        await CompleteInitializeAsync(client, transport);

        var renameTask = client.SetThreadNameAsync("general-thread", "Renamed general chat");
        await transport.WaitForClientMessageCountAsync(3);
        var request = JsonNode.Parse(transport.ClientMessages[2])!.AsObject();
        Assert(ReadString(request, "method") == "thread/name/set", "rename uses thread/name/set");
        Assert(ReadString(request, "params.threadId") == "general-thread", "rename sends the thread id");
        Assert(ReadString(request, "params.name") == "Renamed general chat", "rename sends the new name");
        transport.ServerSend("""{"id":1,"result":{}}""");
        await renameTask;
    }

    private static Task ThreadStoreRenamesChatsAsync()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var settings = new AppSettings
        {
            ProjectThreads =
            [
                PersistedThread("general-thread", ThreadScopeKind.General, string.Empty, "Old general", before),
                PersistedThread("project-thread", ThreadScopeKind.Project, ProjectPath, "Old project", before)
            ]
        };
        var store = new ThreadStore();

        store.Rename(settings, "general-thread", "  Renamed general chat  ");
        store.Rename(settings, "project-thread", "Renamed project chat");

        var general = settings.ProjectThreads.Single(thread => thread.ThreadId == "general-thread");
        var project = settings.ProjectThreads.Single(thread => thread.ThreadId == "project-thread");
        Assert(general.Title == "Renamed general chat", "General title is trimmed and persisted");
        Assert(project.Title == "Renamed project chat", "project title is persisted");
        Assert(general.UpdatedAt > before && project.UpdatedAt > before, "renaming updates recency");
        Assert(
            store.GetThreads(settings, ThreadScopeKey.General).Single().DisplayTitle == "Renamed general chat",
            "General presentation uses the renamed title");
        Assert(
            store.GetThreads(settings, ThreadScopeKey.ForProject(ProjectPath)).Single().DisplayTitle == "Renamed project chat",
            "project presentation uses the renamed title");
        return Task.CompletedTask;
    }

    private static async Task SidebarRenameCommandSupportsBothScopesAsync()
    {
        ProjectThreadViewModel? viewModel = null;
        var renamedIds = new List<string>();
        viewModel = CreateNavigationViewModel(renameThread: () =>
        {
            renamedIds.Add(viewModel!.SelectedThread!.ThreadId);
            return Task.CompletedTask;
        });
        var general = State("general-thread", ThreadScopeKind.General, string.Empty, "General chat");
        var project = State("project-thread", ThreadScopeKind.Project, ProjectPath, "Project chat");
        viewModel.RefreshProjectNavigation(
            [new RecentProject(ProjectPath, "Alpha", DateTimeOffset.UtcNow)],
            [general, project]);

        viewModel.SelectedThread = general;
        Assert(viewModel.RenameThreadCommand.CanExecute(null), "rename is enabled for a General chat");
        viewModel.RenameThreadCommand.Execute(null);
        await WaitUntilAsync(() => renamedIds.Count == 1, "General rename callback");

        viewModel.SelectedThread = project;
        Assert(viewModel.RenameThreadCommand.CanExecute(null), "rename is enabled for a project chat");
        viewModel.RenameThreadCommand.Execute(null);
        await WaitUntilAsync(() => renamedIds.Count == 2, "project rename callback");

        Assert(renamedIds.SequenceEqual(["general-thread", "project-thread"]), "rename targets the selected chat in either scope");
    }

    private static Task ChatMenusExposeRenameAsync() => WpfTestHost.RunAsync(() =>
    {
        ConfigureNavigationResources(Application.Current.Resources);
        var viewModel = CreateNavigationViewModel();
        var general = State("general-thread", ThreadScopeKind.General, string.Empty, "General chat");
        var project = State("project-thread", ThreadScopeKind.Project, ProjectPath, "Project chat");
        viewModel.RefreshProjectNavigation(
            [new RecentProject(ProjectPath, "Alpha", DateTimeOffset.UtcNow)],
            [general, project]);
        viewModel.SetSelectedProjectPath(ProjectPath);
        viewModel.SelectedThread = project;

        var view = new ProjectThreadView
        {
            DataContext = new ProjectContext(viewModel),
            Width = 280,
            Height = 620
        };
        PumpLayout(view);

        var chatActionMenus = FindVisualDescendants<Button>(view)
            .Where(button => AutomationProperties.GetName(button) == "Chat actions")
            .Select(button => button.ContextMenu)
            .OfType<ContextMenu>()
            .ToList();
        Assert(chatActionMenus.Count == 2, "General and project chat action menus are rendered");
        foreach (var menu in chatActionMenus)
        {
            menu.DataContext = viewModel;
        }
        Assert(
            chatActionMenus.All(menu => menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Rename"))),
            "both chat action menus expose Rename");
        Assert(
            chatActionMenus.All(menu => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Rename")).Command == viewModel.RenameThreadCommand),
            "both Rename menu items use the shared selected-thread command");
    });

    private static async Task MainViewModelRenamesBothScopesAsync()
    {
        using var temp = TempWorkspace.Create();
        var generalPath = temp.CreateDirectory("general");
        var projectPath = temp.CreateDirectory("project");
        var settingsStore = new FakeSettingsStore(new AppSettings
        {
            RecentProjects = [new RecentProject(projectPath, "Project", DateTimeOffset.UtcNow)],
            ProjectThreads =
            [
                PersistedThread("general-thread", ThreadScopeKind.General, string.Empty, "Old general", DateTimeOffset.UtcNow, generalPath),
                PersistedThread("project-thread", ThreadScopeKind.Project, projectPath, "Old project", DateTimeOffset.UtcNow, projectPath)
            ]
        });
        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("thread_rename_tests", "Thread Rename Tests", "1.0.0"));
        var interaction = new RenameUserInteractionService("  Renamed general  ", "Renamed project");
        var viewModel = new MainViewModel(
            settingsStore,
            new FakeCodexDiscoveryService(new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation")),
            coordinator,
            new FakeAuthService(new AuthenticationState(AuthReadiness.LikelySignedIn, "Ready", "Test auth", @"C:\Users\Test\.codex")),
            new FakeGitService(temp.Root),
            new FakeWorktreeService(temp.Root, Path.Combine(temp.Root, ".test-worktree")),
            new RecentProjectService(),
            new FakeFolderPicker(temp.Root),
            interaction,
            new FakeThemeService(),
            new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new CodexThreadWorkspace(),
            new FakeTerminalService(),
            logger,
            new GeneralWorkspaceService(generalPath));
        await viewModel.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await WaitUntilAsync(
            () => coordinator.State == AppServerSessionState.Connected,
            "app-server warm-up");

        await RenameSelectedAsync(
            viewModel,
            transport,
            viewModel.ProjectThreads.Single(thread => thread.ThreadId == "general-thread"),
            "Renamed general");
        viewModel.OpenRecentProjectCommand.Execute(projectPath);
        await WaitUntilAsync(
            () => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase),
            "project selection");
        await RenameSelectedAsync(
            viewModel,
            transport,
            viewModel.ProjectThreads.Single(thread => thread.ThreadId == "project-thread"),
            "Renamed project");

        Assert(interaction.InitialValues.SequenceEqual(["Old general", "Old project"]), "rename dialog starts with each current title");
        Assert(
            settingsStore.SavedSettings.ProjectThreads.Single(thread => thread.ThreadId == "general-thread").Title == "Renamed general",
            "General rename is saved");
        Assert(
            settingsStore.SavedSettings.ProjectThreads.Single(thread => thread.ThreadId == "project-thread").Title == "Renamed project",
            "project rename is saved");
        Assert(viewModel.StatusMessage == "Chat renamed", "rename reports completion");
        await viewModel.DisposeAsync();
    }

    private static async Task FirstMessageAutomaticallyRenamesNewChatOnceAsync()
    {
        using var temp = TempWorkspace.Create();
        var generalPath = temp.CreateDirectory("general");
        var settingsStore = new FakeSettingsStore();
        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("thread_rename_tests", "Thread Rename Tests", "1.0.0"));
        var viewModel = new MainViewModel(
            settingsStore,
            new FakeCodexDiscoveryService(new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation")),
            coordinator,
            new FakeAuthService(new AuthenticationState(AuthReadiness.LikelySignedIn, "Ready", "Test auth", @"C:\Users\Test\.codex")),
            new FakeGitService(temp.Root),
            new FakeWorktreeService(temp.Root, Path.Combine(temp.Root, ".test-worktree")),
            new RecentProjectService(),
            new FakeFolderPicker(temp.Root),
            new FakeUserInteractionService(),
            new FakeThemeService(),
            new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new CodexThreadWorkspace(),
            new FakeTerminalService(),
            logger,
            new GeneralWorkspaceService(generalPath));
        await viewModel.InitializeAsync();

        viewModel.PromptText = "  Plan   release\r\nvalidation  ";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"auto-name-thread"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"first-turn"}}}""");

        await WaitUntilAsync(
            () => transport.ClientMessages.Any(message =>
                ReadString(JsonNode.Parse(message)!.AsObject(), "method") == "thread/name/set"),
            "automatic first-message rename");
        var rename = transport.ClientMessages
            .Select(message => JsonNode.Parse(message)!.AsObject())
            .Single(message => ReadString(message, "method") == "thread/name/set");
        Assert(ReadString(rename, "params.threadId") == "auto-name-thread", "automatic rename targets the new chat");
        Assert(ReadString(rename, "params.name") == "Plan release validation", "automatic rename normalizes the first message");
        var renameRequestId = rename["id"]?.ToJsonString()
            ?? throw new InvalidOperationException("automatic rename request did not include an id");
        transport.ServerSend($"{{\"id\":{renameRequestId},\"result\":{{}}}}");
        await WaitUntilAsync(
            () => viewModel.SelectedThread?.Title == "Plan release validation",
            "automatic title presentation");
        await WaitUntilAsync(
            () => settingsStore.SavedSettings.ProjectThreads.Single().Title == "Plan release validation",
            "automatic title persistence");

        transport.ServerSend(
            """{"method":"turn/completed","params":{"threadId":"auto-name-thread","turn":{"id":"first-turn","status":"completed","items":[]}}}""");
        await WaitUntilAsync(() => !viewModel.IsTurnRunning, "first named turn completion");
        await WaitUntilAsync(() => viewModel.SubmitPromptCommand.CanExecute(null), "follow-up submission readiness");

        var beforeFollowUp = transport.ClientMessages.Count;
        viewModel.PromptText = "Run the tests";
        viewModel.SubmitPromptCommand.Execute(null);
        await WaitUntilAsync(
            () => transport.ClientMessages.Skip(beforeFollowUp).Any(message =>
                ReadString(JsonNode.Parse(message)!.AsObject(), "method") == "turn/start"),
            "follow-up turn start");
        var followUp = transport.ClientMessages
            .Skip(beforeFollowUp)
            .Select(message => JsonNode.Parse(message)!.AsObject())
            .Single(message => ReadString(message, "method") == "turn/start");
        var followUpRequestId = followUp["id"]?.ToJsonString()
            ?? throw new InvalidOperationException("follow-up turn request did not include an id");
        transport.ServerSend($"{{\"id\":{followUpRequestId},\"result\":{{\"turn\":{{\"id\":\"follow-up-turn\"}}}}}}");
        await WaitUntilAsync(() => viewModel.IsTurnRunning, "follow-up turn running");
        Assert(
            transport.ClientMessages.Count(message =>
                ReadString(JsonNode.Parse(message)!.AsObject(), "method") == "thread/name/set") == 1,
            "follow-up does not rename the chat again");

        transport.ServerSend(
            """{"method":"turn/completed","params":{"threadId":"auto-name-thread","turn":{"id":"follow-up-turn","status":"completed","items":[]}}}""");
        await WaitUntilAsync(() => !viewModel.IsTurnRunning, "follow-up turn completion");
        await viewModel.DisposeAsync();
    }

    private static async Task RenameSelectedAsync(
        MainViewModel viewModel,
        FakeAppServerTransport transport,
        ProjectThreadState thread,
        string expectedName)
    {
        viewModel.SelectedThread = thread;
        var previousRequestCount = transport.ClientMessages.Count;
        viewModel.RenameThreadCommand.Execute(null);
        await WaitUntilAsync(
            () => transport.ClientMessages.Skip(previousRequestCount).Any(message =>
                ReadString(JsonNode.Parse(message)!.AsObject(), "method") == "thread/name/set"),
            $"{thread.ThreadId} rename request");
        var request = transport.ClientMessages
            .Skip(previousRequestCount)
            .Select(message => JsonNode.Parse(message)!.AsObject())
            .Single(message => ReadString(message, "method") == "thread/name/set");
        Assert(ReadString(request, "params.threadId") == thread.ThreadId, "rename request targets the selected thread");
        Assert(ReadString(request, "params.name") == expectedName, "rename request sends the trimmed title");
        var requestId = request["id"]?.ToJsonString()
            ?? throw new InvalidOperationException("rename request did not include an id");
        transport.ServerSend($"{{\"id\":{requestId},\"result\":{{}}}}");
        await WaitUntilAsync(
            () => viewModel.ProjectThreads.Single(item => item.ThreadId == thread.ThreadId).Title == expectedName,
            $"{thread.ThreadId} persisted rename");
    }

    private static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initializeTask = client.InitializeAsync(CodexInitializeOptions.Default);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await initializeTask;
    }

    private static ProjectThreadViewModel CreateNavigationViewModel(Func<Task>? renameThread = null) => new(
        () => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => true,
        () => true,
        () => true,
        () => true,
        () => true,
        () => true,
        _ => { },
        renameThread: renameThread ?? (() => Task.CompletedTask),
        canRenameThread: () => true);

    private static PersistedProjectThread PersistedThread(
        string id,
        ThreadScopeKind scopeKind,
        string projectPath,
        string title,
        DateTimeOffset updatedAt,
        string? workspacePath = null) => new()
    {
        ScopeKind = scopeKind,
        ProjectPath = projectPath,
        ThreadId = id,
        Title = title,
        Mode = scopeKind == ThreadScopeKind.General ? "general" : "local",
        WorkspacePath = workspacePath ?? (scopeKind == ThreadScopeKind.General ? GeneralPath : projectPath),
        UpdatedAt = updatedAt
    };

    private static ProjectThreadState State(
        string id,
        ThreadScopeKind scopeKind,
        string projectPath,
        string title) => new()
    {
        ScopeKind = scopeKind,
        ProjectPath = projectPath,
        ThreadId = id,
        Title = title,
        Mode = scopeKind == ThreadScopeKind.General ? "general" : "local",
        WorkspacePath = scopeKind == ThreadScopeKind.General ? GeneralPath : projectPath
    };

    private static string? ReadString(JsonObject source, string path)
    {
        JsonNode? current = source;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
        }
        return current?.GetValue<string>();
    }

    private static void ConfigureNavigationResources(ResourceDictionary resources)
    {
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["CompactButton"] = new Style(typeof(Button));
        resources["CreateThreadButton"] = new Style(typeof(Button));
        resources["ProjectActionButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = new Style(typeof(TextBlock));
        resources["StatePill"] = new Style(typeof(Border));
    }

    private static void PumpLayout(FrameworkElement element)
    {
        var available = new Size(element.Width, element.Height);
        element.Measure(available);
        element.Arrange(new Rect(available));
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.Measure(available);
        element.Arrange(new Rect(available));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private sealed record ProjectContext(ProjectThreadViewModel ProjectWorkspace);

    private sealed class RenameUserInteractionService(params string?[] results) : IUserInteractionService
    {
        private readonly Queue<string?> results = new(results);

        public List<string> InitialValues { get; } = [];

        public bool ConfirmDestructiveAction(string title, string message) => true;

        public string? PromptForText(string title, string message, string initialValue)
        {
            InitialValues.Add(initialValue);
            return results.Dequeue();
        }

        public void OpenInEditor(string path)
        {
        }

        public void OpenExternalUri(Uri uri)
        {
        }

        public void RevealInExplorer(string path)
        {
        }
    }

    private const string ProjectPath = @"C:\Work\Alpha";
    private const string GeneralPath = @"C:\AppData\SynthiaCode\General";
}
