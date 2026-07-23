using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

internal static class ChatManagementSearchTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("thread store pins and permanently deletes chats", ThreadStorePinsAndDeletesChatsAsync),
        ("sidebar pin and delete commands delegate selected chat actions", SidebarCommandsDelegateSelectedChatActionsAsync),
        ("main view model persists pins and deletes archived chats", MainViewModelPersistsPinsAndDeletesArchivedChatsAsync),
        ("cross-chat search finds content and opens results across scopes", CrossChatSearchFindsAndOpensAcrossScopesAsync),
        ("find in chat counts navigates and clears transcript matches", FindInChatCountsNavigatesAndClearsMatchesAsync)
    ];

    private static Task ThreadStorePinsAndDeletesChatsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new AppSettings
        {
            ProjectThreads =
            [
                PersistedThread("chat-newest", "Newest", now),
                PersistedThread("chat-older", "Older", now.AddMinutes(-5))
            ]
        };
        var store = new ThreadStore();

        store.SetPinned(settings, "chat-older", pinned: true);

        Assert(settings.ProjectThreads.Single(thread => thread.ThreadId == "chat-older").IsPinned, "pin state is persisted");
        Assert(
            store.GetProjectThreads(settings, ProjectPath).Select(thread => thread.ThreadId)
                .SequenceEqual(["chat-older", "chat-newest"]),
            "pinned chats sort before newer unpinned chats");

        Assert(store.Delete(settings, "chat-older"), "existing chat is deleted");
        Assert(!settings.ProjectThreads.Any(thread => thread.ThreadId == "chat-older"), "deleted chat is removed from persistence");
        Assert(!store.Delete(settings, "chat-missing"), "deleting a missing chat is a no-op");
        return Task.CompletedTask;
    }

    private static async Task SidebarCommandsDelegateSelectedChatActionsAsync()
    {
        ProjectThreadViewModel? viewModel = null;
        var pinCount = 0;
        var deleteCount = 0;
        viewModel = CreateNavigationViewModel(
            togglePinThread: () =>
            {
                pinCount++;
                viewModel!.SelectedThread!.IsPinned = !viewModel.SelectedThread.IsPinned;
                return Task.CompletedTask;
            },
            deleteThread: () =>
            {
                deleteCount++;
                return Task.CompletedTask;
            });
        var thread = new ProjectThreadState
        {
            ProjectPath = ProjectPath,
            ThreadId = "selected-chat",
            Title = "Selected chat",
            WorkspacePath = ProjectPath
        };
        viewModel.ReplaceThreads([thread], thread.ThreadId);

        Assert(viewModel.TogglePinThreadCommand.CanExecute(null), "pin is enabled for the selected chat");
        viewModel.TogglePinThreadCommand.Execute(null);
        await WaitUntilAsync(() => pinCount == 1, "pin callback");
        Assert(thread.IsPinned, "pin callback updates the selected chat");
        Assert(viewModel.PinActionLabel == "Unpin", "pin action label reflects the selected chat state");

        Assert(viewModel.DeleteThreadCommand.CanExecute(null), "delete is enabled for the selected chat");
        viewModel.DeleteThreadCommand.Execute(null);
        await WaitUntilAsync(() => deleteCount == 1, "delete callback");
    }

    private static async Task CrossChatSearchFindsAndOpensAcrossScopesAsync()
    {
        ProjectThreadViewModel? viewModel = null;
        var projectThread = Thread(
            "project-result",
            ThreadScopeKind.Project,
            ProjectPath,
            "Parser cleanup",
            "The user asked for a durable NEEDLE parser.");
        var generalThread = Thread(
            "general-result",
            ThreadScopeKind.General,
            string.Empty,
            "Release notes",
            "No matching prompt.",
            "The assistant found another needle in the release summary.");
        var ignoredThread = Thread(
            "ignored",
            ThreadScopeKind.Project,
            ProjectPath,
            "Unrelated",
            "Nothing to see here.");

        viewModel = CreateNavigationViewModel(openProject: parameter =>
        {
            var path = (string)parameter!;
            viewModel!.SetSelectedProjectPath(path);
            viewModel.ReplaceThreads([projectThread, ignoredThread], projectThread.ThreadId);
            return Task.CompletedTask;
        });
        viewModel.RefreshProjectNavigation(
            [new RecentProject(ProjectPath, "Alpha", DateTimeOffset.UtcNow)],
            [projectThread, generalThread, ignoredThread]);

        viewModel.ChatSearchText = "needle";

        Assert(viewModel.ChatSearchResults.Count == 2, "search returns matching chats from General and projects");
        Assert(
            viewModel.ChatSearchResults.Select(result => result.ThreadId)
                .OrderBy(id => id)
                .SequenceEqual(["general-result", "project-result"]),
            "search matches both user and assistant conversation content");
        var projectResult = viewModel.ChatSearchResults.Single(result => result.ThreadId == "project-result");
        Assert(projectResult.ScopeLabel == "Alpha", "project result identifies its scope");
        Assert(projectResult.Snippet.Contains("NEEDLE", StringComparison.Ordinal), "result includes matching context");

        viewModel.OpenChatSearchResultCommand.Execute(projectResult);
        await WaitUntilAsync(
            () => viewModel.SelectedThread?.ThreadId == projectThread.ThreadId,
            "cross-project search result selection");
        Assert(
            ProjectNavigationItemViewModel.PathsEqual(viewModel.SelectedProjectPath, ProjectPath),
            "opening a project result switches project scope");
        Assert(string.IsNullOrEmpty(viewModel.ChatSearchText), "opening a result clears the search");
    }

    private static async Task MainViewModelPersistsPinsAndDeletesArchivedChatsAsync()
    {
        using var temp = TempWorkspace.Create();
        var generalPath = temp.CreateDirectory("general");
        var settingsStore = new FakeSettingsStore(new AppSettings
        {
            ProjectThreads =
            [
                new PersistedProjectThread
                {
                    ScopeKind = ThreadScopeKind.General,
                    ThreadId = "archived-chat",
                    Title = "Archived chat",
                    IsArchived = true,
                    Mode = "general",
                    WorkspacePath = generalPath
                }
            ]
        });
        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("chat_management_tests", "Chat Management Tests", "1.0.0"));
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
        viewModel.SelectedThread = viewModel.ProjectThreads.Single();

        viewModel.TogglePinThreadCommand.Execute(null);
        await WaitUntilAsync(
            () => settingsStore.SavedSettings.ProjectThreads.Single().IsPinned,
            "persisted chat pin");
        Assert(viewModel.ProjectThreads.Single().IsPinned, "pin refreshes sidebar presentation");

        viewModel.DeleteThreadCommand.Execute(null);
        await WaitUntilAsync(
            () => settingsStore.SavedSettings.ProjectThreads.Count == 0,
            "archived chat deletion");
        Assert(viewModel.ProjectThreads.Count == 0, "delete removes the chat from navigation");
        Assert(viewModel.SelectedThread is null, "delete clears selection when no chats remain");
        Assert(viewModel.StatusMessage == "Chat deleted", "delete reports completion");
        await viewModel.DisposeAsync();
    }

    private static Task FindInChatCountsNavigatesAndClearsMatchesAsync()
    {
        var service = new CodexThreadService();
        service.Restore(
            "find-chat",
            "Second needle.",
            null,
            null,
            conversationTurns:
            [
                Snapshot("turn-one", "Needle in the prompt.", "A needle in the answer."),
                Snapshot("turn-two", "No match here.", "Second needle.")
            ]);
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false);
        viewModel.UseThreadService(service);

        viewModel.OpenFindInChatCommand.Execute(null);
        viewModel.FindInChatText = "needle";

        Assert(viewModel.IsFindInChatOpen, "find toolbar opens");
        Assert(viewModel.FindInChatMatchCount == 3, "find counts every user and assistant match");
        Assert(viewModel.CurrentFindInChatMatchNumber == 1, "find selects the first match");
        Assert(viewModel.CurrentFindInChatTurn?.TurnId == "turn-one", "first match points to the first turn");
        Assert(service.ConversationTurns[0].IsCurrentFindMatch, "current turn is marked for transcript highlighting");
        Assert(service.ConversationTurns[1].IsFindMatch, "other matching turns are marked");

        viewModel.FindNextCommand.Execute(null);
        Assert(viewModel.CurrentFindInChatMatchNumber == 2, "next advances one occurrence");
        viewModel.FindNextCommand.Execute(null);
        Assert(viewModel.CurrentFindInChatMatchNumber == 3, "next advances into the next turn");
        Assert(viewModel.CurrentFindInChatTurn?.TurnId == "turn-two", "next scroll target follows the selected match");
        viewModel.FindNextCommand.Execute(null);
        Assert(viewModel.CurrentFindInChatMatchNumber == 1, "next wraps after the final match");
        viewModel.FindPreviousCommand.Execute(null);
        Assert(viewModel.CurrentFindInChatMatchNumber == 3, "previous wraps before the first match");

        viewModel.CloseFindInChatCommand.Execute(null);
        Assert(!viewModel.IsFindInChatOpen, "find toolbar closes");
        Assert(viewModel.FindInChatMatchCount == 0, "closing find clears matches");
        Assert(service.ConversationTurns.All(turn => !turn.IsFindMatch && !turn.IsCurrentFindMatch), "closing find clears transcript highlights");
        return Task.CompletedTask;
    }

    private static ProjectThreadViewModel CreateNavigationViewModel(
        Func<object?, Task>? openProject = null,
        Func<Task>? togglePinThread = null,
        Func<Task>? deleteThread = null) => new(
        () => Task.CompletedTask,
        openProject ?? (_ => Task.CompletedTask),
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
        togglePinThread,
        deleteThread,
        () => true,
        () => true);

    private static PersistedProjectThread PersistedThread(string id, string title, DateTimeOffset updatedAt) => new()
    {
        ProjectPath = ProjectPath,
        ThreadId = id,
        Title = title,
        WorkspacePath = ProjectPath,
        UpdatedAt = updatedAt
    };

    private static ProjectThreadState Thread(
        string id,
        ThreadScopeKind scopeKind,
        string projectPath,
        string title,
        string userPrompt,
        string assistantResponse = "") => new()
    {
        ScopeKind = scopeKind,
        ProjectPath = projectPath,
        ThreadId = id,
        Title = title,
        WorkspacePath = scopeKind == ThreadScopeKind.General ? GeneralPath : projectPath,
        ConversationTurns = [Snapshot($"{id}-turn", userPrompt, assistantResponse)],
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static CodexConversationTurnSnapshot Snapshot(string id, string userPrompt, string assistantResponse) => new()
    {
        TurnId = id,
        UserPrompt = userPrompt,
        AssistantResponse = assistantResponse,
        Status = CodexTurnStatus.Completed
    };

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

    private const string ProjectPath = @"C:\Work\Alpha";
    private const string GeneralPath = @"C:\AppData\SynthiaCode\General";
}
