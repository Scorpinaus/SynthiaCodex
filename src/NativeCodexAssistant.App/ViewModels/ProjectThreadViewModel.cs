using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class ProjectThreadViewModel : ObservableObject
{
    private readonly Action<ProjectThreadState?> selectionChanged;
    private readonly IReadOnlyList<AsyncRelayCommand> statefulCommands;
    private string? selectedProjectPath;
    private string newThreadWorkspaceMode = "Current checkout";
    private ProjectThreadState? selectedThread;

    public ProjectThreadViewModel(
        Func<Task> browseProject,
        Func<object?, Task> openRecentProject,
        Func<Task> createThread,
        Func<Task> resumeThread,
        Func<Task> forkThread,
        Func<Task> archiveThread,
        Func<Task> unarchiveThread,
        Func<Task> removeWorktree,
        Func<bool> canManageThreads,
        Func<bool> canUseSelectedThread,
        Func<bool> canArchiveSelectedThread,
        Func<bool> canUnarchiveSelectedThread,
        Func<bool> canRemoveWorktree,
        Action<ProjectThreadState?> selectionChanged)
    {
        this.selectionChanged = selectionChanged;
        BrowseProjectCommand = new AsyncRelayCommand(browseProject);
        OpenRecentProjectCommand = new AsyncRelayCommand(openRecentProject);
        NewThreadCommand = new AsyncRelayCommand(createThread, canManageThreads);
        ResumeThreadCommand = new AsyncRelayCommand(resumeThread, canUseSelectedThread);
        ForkThreadCommand = new AsyncRelayCommand(forkThread, canUseSelectedThread);
        ArchiveThreadCommand = new AsyncRelayCommand(archiveThread, canArchiveSelectedThread);
        UnarchiveThreadCommand = new AsyncRelayCommand(unarchiveThread, canUnarchiveSelectedThread);
        RemoveWorktreeCommand = new AsyncRelayCommand(removeWorktree, canRemoveWorktree);
        statefulCommands =
        [
            (AsyncRelayCommand)NewThreadCommand,
            (AsyncRelayCommand)ResumeThreadCommand,
            (AsyncRelayCommand)ForkThreadCommand,
            (AsyncRelayCommand)ArchiveThreadCommand,
            (AsyncRelayCommand)UnarchiveThreadCommand,
            (AsyncRelayCommand)RemoveWorktreeCommand
        ];
    }

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<ProjectThreadState> Threads { get; } = [];

    public ICommand BrowseProjectCommand { get; }
    public ICommand OpenRecentProjectCommand { get; }
    public ICommand NewThreadCommand { get; }
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
                RaiseCommandStates();
            }
        }
    }

    public string SelectedProjectName =>
        string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? "No project selected"
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
                RaiseCommandStates();
                selectionChanged(value);
            }
        }
    }

    public string ActiveWorkspacePath => SelectedThread?.WorkspacePath ?? SelectedProjectPath ?? "No workspace selected";

    public string ActiveWorkspaceLabel => SelectedThread?.WorkspaceModeLabel ?? "Current checkout";

    public void SetSelectedProjectPath(string? path) => SelectedProjectPath = path;

    public void RefreshRecentProjects(IEnumerable<RecentProject> projects)
    {
        RecentProjects.Clear();
        foreach (var project in projects)
        {
            RecentProjects.Add(project);
        }
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
}
