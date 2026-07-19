using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.ViewModels;

public sealed class ProjectThreadViewModel : ObservableObject
{
    private readonly Action<ProjectThreadState?> selectionChanged;
    private readonly IReadOnlyList<AsyncRelayCommand> statefulCommands;
    private string? selectedProjectPath;
    private string? generalWorkspacePath;
    private string newThreadWorkspaceMode = "Current checkout";
    private ProjectThreadState? selectedThread;

    public ProjectThreadViewModel(
        Func<Task> browseProject,
        Func<object?, Task> openRecentProject,
        Func<Task> createThread,
        Func<Task> createGeneralThread,
        Func<Task> createProjectThread,
        Func<Task> resumeThread,
        Func<Task> forkThread,
        Func<Task> archiveThread,
        Func<Task> unarchiveThread,
        Func<Task> removeWorktree,
        Func<bool> canCreateThread,
        Func<bool> canCreateGeneralThread,
        Func<bool> canUseSelectedThread,
        Func<bool> canArchiveSelectedThread,
        Func<bool> canUnarchiveSelectedThread,
        Func<bool> canRemoveWorktree,
        Action<ProjectThreadState?> selectionChanged)
    {
        this.selectionChanged = selectionChanged;
        BrowseProjectCommand = new AsyncRelayCommand(browseProject);
        OpenRecentProjectCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is not string path || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var project = Projects.FirstOrDefault(item => ProjectNavigationItemViewModel.PathsEqual(item.Path, path));
            if (ProjectNavigationItemViewModel.PathsEqual(SelectedProjectPath, path))
            {
                if (project is not null)
                {
                    project.IsExpanded = !project.IsExpanded;
                }
                return;
            }

            await openRecentProject(parameter).ConfigureAwait(true);
            SynchronizeProjectSelection(expandSelected: true);
        });
        NewThreadCommand = new AsyncRelayCommand(createThread, canCreateThread);
        NewGeneralThreadCommand = new AsyncRelayCommand(createGeneralThread, canCreateGeneralThread);
        NewThreadForProjectCommand = new AsyncRelayCommand(parameter =>
            CreateThreadForProjectAsync(parameter, "Current checkout", openRecentProject, createProjectThread));
        NewWorktreeThreadForProjectCommand = new AsyncRelayCommand(parameter =>
            CreateThreadForProjectAsync(parameter, "New worktree", openRecentProject, createProjectThread));
        ResumeThreadCommand = new AsyncRelayCommand(resumeThread, canUseSelectedThread);
        ForkThreadCommand = new AsyncRelayCommand(forkThread, canUseSelectedThread);
        ArchiveThreadCommand = new AsyncRelayCommand(archiveThread, canArchiveSelectedThread);
        UnarchiveThreadCommand = new AsyncRelayCommand(unarchiveThread, canUnarchiveSelectedThread);
        RemoveWorktreeCommand = new AsyncRelayCommand(removeWorktree, canRemoveWorktree);
        statefulCommands =
        [
            (AsyncRelayCommand)NewThreadCommand,
            (AsyncRelayCommand)NewGeneralThreadCommand,
            (AsyncRelayCommand)ResumeThreadCommand,
            (AsyncRelayCommand)ForkThreadCommand,
            (AsyncRelayCommand)ArchiveThreadCommand,
            (AsyncRelayCommand)UnarchiveThreadCommand,
            (AsyncRelayCommand)RemoveWorktreeCommand
        ];
    }

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<ProjectThreadState> Threads { get; } = [];

    public ObservableCollection<ProjectThreadState> GeneralThreads { get; } = [];

    public ObservableCollection<ProjectNavigationItemViewModel> Projects { get; } = [];

    public ICommand BrowseProjectCommand { get; }
    public ICommand OpenRecentProjectCommand { get; }
    public ICommand NewThreadCommand { get; }
    public ICommand NewGeneralThreadCommand { get; }
    public ICommand NewThreadForProjectCommand { get; }
    public ICommand NewWorktreeThreadForProjectCommand { get; }
    public ICommand ResumeThreadCommand { get; }
    public ICommand ForkThreadCommand { get; }
    public ICommand ArchiveThreadCommand { get; }
    public ICommand UnarchiveThreadCommand { get; }
    public ICommand RemoveWorktreeCommand { get; }

    public IReadOnlyList<string> WorkspaceModeOptions { get; } = ["Current checkout", "New worktree"];

    public string? SelectedProjectPath
    {
        get => selectedProjectPath;
        private set
        {
            if (SetProperty(ref selectedProjectPath, value))
            {
                OnPropertyChanged(nameof(SelectedProjectName));
                OnPropertyChanged(nameof(ActiveWorkspacePath));
                SynchronizeProjectSelection(expandSelected: true);
                RaiseCommandStates();
            }
        }
    }

    public string SelectedProjectName =>
        string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? "General"
            : new DirectoryInfo(SelectedProjectPath).Name;

    public string NewThreadWorkspaceMode
    {
        get => newThreadWorkspaceMode;
        set => SetProperty(ref newThreadWorkspaceMode, value == "New worktree" ? value : "Current checkout");
    }

    public ProjectThreadState? SelectedThread
    {
        get => selectedThread;
        set
        {
            if (SetProperty(ref selectedThread, value))
            {
                OnPropertyChanged(nameof(ActiveWorkspacePath));
                OnPropertyChanged(nameof(ActiveWorkspaceLabel));
                OnPropertyChanged(nameof(SelectedThreadTitle));
                OnPropertyChanged(nameof(SelectedGeneralThread));
                RaiseCommandStates();
                selectionChanged(value);
            }
        }
    }

    public ProjectThreadState? SelectedGeneralThread
    {
        get => SelectedThread?.ScopeKind == ThreadScopeKind.General ? SelectedThread : null;
        set
        {
            if (value is not null)
            {
                SelectedThread = value;
            }
        }
    }

    public string ActiveWorkspacePath => SelectedThread?.WorkspacePath ?? SelectedProjectPath ?? generalWorkspacePath ?? "General workspace unavailable";

    public string ActiveWorkspaceLabel => SelectedThread?.WorkspaceModeLabel
        ?? (string.IsNullOrWhiteSpace(SelectedProjectPath) ? "General workspace" : "Current checkout");

    public string SelectedThreadTitle => SelectedThread?.DisplayTitle ?? "No thread selected";

    public void SetSelectedProjectPath(string? path) => SelectedProjectPath = path;

    public void SetGeneralWorkspacePath(string? path)
    {
        generalWorkspacePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        OnPropertyChanged(nameof(ActiveWorkspacePath));
    }

    public void RefreshRecentProjects(IEnumerable<RecentProject> projects)
    {
        RecentProjects.Clear();
        foreach (var project in projects)
        {
            RecentProjects.Add(project);
        }
    }

    public void RefreshProjectNavigation(
        IEnumerable<RecentProject> projects,
        IEnumerable<ProjectThreadState> threads)
    {
        var projectList = projects.ToList();
        var threadList = threads.ToList();
        GeneralThreads.Clear();
        foreach (var thread in threadList
            .Where(thread => thread.ScopeKind == ThreadScopeKind.General)
            .OrderByDescending(thread => thread.IsPinned)
            .ThenByDescending(thread => thread.UpdatedAt))
        {
            GeneralThreads.Add(thread);
        }
        OnPropertyChanged(nameof(SelectedGeneralThread));
        var existing = Projects.ToList();
        var refreshed = new List<ProjectNavigationItemViewModel>();

        foreach (var project in projectList)
        {
            var item = existing.FirstOrDefault(candidate =>
                ProjectNavigationItemViewModel.PathsEqual(candidate.Path, project.Path))
                ?? new ProjectNavigationItemViewModel(project);
            item.Update(
                project,
                threadList.Where(thread =>
                    thread.ScopeKind == ThreadScopeKind.Project &&
                    ProjectNavigationItemViewModel.PathsEqual(thread.ProjectPath, project.Path)));
            refreshed.Add(item);
        }

        foreach (var removed in existing.Except(refreshed))
        {
            removed.Dispose();
        }

        Projects.Clear();
        foreach (var item in refreshed)
        {
            Projects.Add(item);
        }

        SynchronizeProjectSelection(expandSelected: true);
    }

    public void ReplaceThreads(IEnumerable<ProjectThreadState> threads, string? selectedThreadId)
    {
        Threads.Clear();
        foreach (var thread in threads)
        {
            Threads.Add(thread);
        }

        SelectedThread = Threads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, selectedThreadId, StringComparison.Ordinal));
    }

    public void RaiseCommandStates()
    {
        foreach (var command in statefulCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void SynchronizeProjectSelection(bool expandSelected)
    {
        foreach (var project in Projects)
        {
            var selected = ProjectNavigationItemViewModel.PathsEqual(project.Path, SelectedProjectPath);
            project.IsSelected = selected;
            if (selected && expandSelected)
            {
                project.IsExpanded = true;
            }
            else if (!selected)
            {
                project.IsExpanded = false;
            }
        }
    }

    private async Task CreateThreadForProjectAsync(
        object? parameter,
        string workspaceMode,
        Func<object?, Task> openRecentProject,
        Func<Task> createThread)
    {
        if (parameter is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!ProjectNavigationItemViewModel.PathsEqual(SelectedProjectPath, path))
        {
            await openRecentProject(path).ConfigureAwait(true);
        }

        if (!ProjectNavigationItemViewModel.PathsEqual(SelectedProjectPath, path))
        {
            return;
        }

        NewThreadWorkspaceMode = workspaceMode;
        await createThread().ConfigureAwait(true);
    }
}
