using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;

internal static class Phase5DNavigationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("project navigation groups threads and expands selection", NavigationGroupsThreadsAsync),
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
