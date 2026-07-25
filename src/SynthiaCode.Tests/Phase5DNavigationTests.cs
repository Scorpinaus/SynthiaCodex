using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;

internal static class Phase5DNavigationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("project navigation groups threads and expands selection", NavigationGroupsThreadsAsync),
        ("expanding a project retains the active chat", ExpandingProjectRetainsActiveChatAsync),
        ("chat and project sections toggle independently", NavigationSectionsToggleIndependentlyAsync),
        ("project navigation preserves compact actionable statuses", CompactStatusesAreActionableAsync),
        ("project plus creates current-checkout threads", ProjectCreationActionsChooseWorkspaceAsync)
    ];

    private static Task NavigationGroupsThreadsAsync()
    {
        ProjectThreadViewModel? viewModel = null;
        viewModel = CreateViewModel(parameter =>
        {
            viewModel!.SetSelectedProjectPath((string)parameter!);
            return Task.CompletedTask;
        });

        var alphaPath = @"C:\Work\Alpha";
        var unicodePath = @"C:\Work\项目 Beta";
        var projects = new[]
        {
            new RecentProject(alphaPath, "Alpha", DateTimeOffset.UtcNow),
            new RecentProject(unicodePath, "项目 Beta", DateTimeOffset.UtcNow.AddMinutes(-1))
        };
        var alphaThread = new ProjectThreadState
        {
            ProjectPath = @"c:\work\ALPHA",
            ThreadId = "alpha-thread",
            Title = "Alpha task",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var betaThread = new ProjectThreadState
        {
            ProjectPath = unicodePath,
            ThreadId = "beta-thread",
            Title = "Beta task",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        viewModel.RefreshProjectNavigation(projects, [alphaThread, betaThread]);
        viewModel.SetSelectedProjectPath(alphaPath);

        Assert(viewModel.Projects.Count == 2, "two project rows are retained");
        Assert(viewModel.Projects[0].Threads.Single().ThreadId == "alpha-thread", "case-insensitive paths group the alpha thread");
        Assert(viewModel.Projects[1].Threads.Single().ThreadId == "beta-thread", "unicode paths group the beta thread");
        Assert(viewModel.Projects[0].IsSelected && viewModel.Projects[0].IsExpanded, "selected project expands");
        Assert(!viewModel.Projects[1].IsExpanded, "inactive project remains collapsed");

        viewModel.OpenRecentProjectCommand.Execute(unicodePath);
        Assert(viewModel.Projects[1].IsSelected && viewModel.Projects[1].IsExpanded, "clicking another project selects and expands it");
        Assert(!viewModel.Projects[0].IsExpanded, "the previous project collapses");

        viewModel.OpenRecentProjectCommand.Execute(unicodePath);
        Assert(!viewModel.Projects[1].IsExpanded, "clicking the active project toggles its disclosure");
        return Task.CompletedTask;
    }

    private static Task ExpandingProjectRetainsActiveChatAsync()
    {
        ProjectThreadViewModel? viewModel = null;
        var openedProjects = new List<string>();
        viewModel = CreateViewModel(parameter =>
        {
            openedProjects.Add((string)parameter!);
            viewModel!.SetSelectedProjectPath((string)parameter!);
            return Task.CompletedTask;
        });

        var activePath = @"C:\Work\Active";
        var otherPath = @"C:\Work\Other";
        var activeThread = new ProjectThreadState
        {
            ProjectPath = activePath,
            ThreadId = "active-thread",
            Title = "Active chat",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var otherThread = new ProjectThreadState
        {
            ProjectPath = otherPath,
            ThreadId = "other-thread",
            Title = "Newest other chat",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        viewModel.RefreshProjectNavigation(
            [
                new RecentProject(activePath, "Active", DateTimeOffset.UtcNow),
                new RecentProject(otherPath, "Other", DateTimeOffset.UtcNow)
            ],
            [activeThread, otherThread]);
        viewModel.SetSelectedProjectPath(activePath);
        viewModel.SelectedThread = activeThread;

        viewModel.ToggleProjectExpansionCommand.Execute(otherPath);

        Assert(ReferenceEquals(viewModel.SelectedThread, activeThread), "expanding another project retains the active chat");
        Assert(ProjectNavigationItemViewModel.PathsEqual(viewModel.SelectedProjectPath, activePath), "expanding another project retains the active workspace");
        Assert(viewModel.Projects.Single(project => ProjectNavigationItemViewModel.PathsEqual(project.Path, otherPath)).IsExpanded, "the requested project expands");
        Assert(viewModel.Projects.Single(project => ProjectNavigationItemViewModel.PathsEqual(project.Path, activePath)).IsExpanded, "the active project remains expanded");
        Assert(openedProjects.Count == 0, "project disclosure does not invoke workspace activation");

        viewModel.ToggleProjectExpansionCommand.Execute(otherPath);
        Assert(!viewModel.Projects.Single(project => ProjectNavigationItemViewModel.PathsEqual(project.Path, otherPath)).IsExpanded, "a second disclosure click collapses the project");
        Assert(ReferenceEquals(viewModel.SelectedThread, activeThread), "collapsing another project still retains the active chat");
        return Task.CompletedTask;
    }

    private static Task CompactStatusesAreActionableAsync()
    {
        var thread = new ProjectThreadState { TurnStatus = "Completed" };
        Assert(!thread.HasActionableStatus, "completed status is hidden");

        thread.TurnStatus = "Idle";
        Assert(!thread.HasActionableStatus, "idle status is hidden");

        thread.IsRunning = true;
        Assert(thread.HasActionableStatus, "running status remains visible");
        thread.IsRunning = false;

        thread.TurnStatus = "Failed";
        Assert(thread.HasActionableStatus, "failed status remains visible");
        thread.TurnStatus = "Cancelled";
        Assert(thread.HasActionableStatus, "cancelled status remains visible");
        thread.TurnStatus = "Completed";
        thread.IsArchived = true;
        Assert(thread.HasActionableStatus && thread.ActivityLabel == "Archived", "archived status remains visible");
        return Task.CompletedTask;
    }

    private static Task NavigationSectionsToggleIndependentlyAsync()
    {
        var viewModel = CreateViewModel(_ => Task.CompletedTask);

        Assert(viewModel.IsChatsExpanded, "Chats starts expanded");
        Assert(viewModel.IsProjectsExpanded, "Projects starts expanded");
        Assert(viewModel.ChatsChevron == "▾", "expanded Chats uses a down disclosure indicator");
        Assert(viewModel.ProjectsChevron == "▾", "expanded Projects uses a down disclosure indicator");

        viewModel.ToggleChatsCommand.Execute(null);
        Assert(!viewModel.IsChatsExpanded, "Chats collapses from its header");
        Assert(viewModel.IsProjectsExpanded, "collapsing Chats does not collapse Projects");
        Assert(viewModel.ChatsChevron == "▸", "collapsed Chats uses a right disclosure indicator");

        viewModel.ToggleProjectsCommand.Execute(null);
        Assert(!viewModel.IsProjectsExpanded, "Projects collapses from its header");
        Assert(!viewModel.IsChatsExpanded, "collapsing Projects does not expand Chats");
        Assert(viewModel.ProjectsChevron == "▸", "collapsed Projects uses a right disclosure indicator");

        viewModel.ToggleChatsCommand.Execute(null);
        viewModel.ToggleProjectsCommand.Execute(null);
        Assert(viewModel.IsChatsExpanded && viewModel.IsProjectsExpanded, "both sections can be reopened independently");
        return Task.CompletedTask;
    }

    private static Task ProjectCreationActionsChooseWorkspaceAsync()
    {
        ProjectThreadViewModel? viewModel = null;
        var createdModes = new List<string>();
        viewModel = CreateViewModel(
            parameter =>
            {
                viewModel!.SetSelectedProjectPath((string)parameter!);
                return Task.CompletedTask;
            },
            () =>
            {
                createdModes.Add(viewModel!.NewThreadWorkspaceMode);
                return Task.CompletedTask;
            });

        var firstPath = @"C:\Work\First";
        var secondPath = @"C:\Work\Second";
        viewModel.RefreshProjectNavigation(
            [
                new RecentProject(firstPath, "First", DateTimeOffset.UtcNow),
                new RecentProject(secondPath, "Second", DateTimeOffset.UtcNow)
            ],
            []);
        viewModel.SetSelectedProjectPath(firstPath);

        viewModel.NewThreadForProjectCommand.Execute(secondPath);
        Assert(ProjectNavigationItemViewModel.PathsEqual(viewModel.SelectedProjectPath, secondPath), "plus selects its owning project");
        Assert(createdModes.SequenceEqual(["Current checkout"]), "plus creates in the current checkout");

        viewModel.NewWorktreeThreadForProjectCommand.Execute(firstPath);
        Assert(ProjectNavigationItemViewModel.PathsEqual(viewModel.SelectedProjectPath, firstPath), "worktree action selects its owning project");
        Assert(createdModes.SequenceEqual(["Current checkout", "New worktree"]), "advanced action creates a worktree thread");
        return Task.CompletedTask;
    }

    private static ProjectThreadViewModel CreateViewModel(
        Func<object?, Task> openProject,
        Func<Task>? createThread = null) => new(
        () => Task.CompletedTask,
        openProject,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        createThread ?? (() => Task.CompletedTask),
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
        _ => { });

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
