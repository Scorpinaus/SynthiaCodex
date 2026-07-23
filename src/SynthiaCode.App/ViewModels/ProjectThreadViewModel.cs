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
    private readonly Func<object?, Task> openRecentProject;
    private readonly List<ProjectThreadState> navigationThreads = [];
    private string? selectedProjectPath;
    private string? generalWorkspacePath;
    private bool isChatsExpanded = true;
    private bool isProjectsExpanded = true;
    private string chatSearchText = string.Empty;
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
        Action<ProjectThreadState?> selectionChanged,
        Func<Task>? togglePinThread = null,
        Func<Task>? deleteThread = null,
        Func<bool>? canTogglePinThread = null,
        Func<bool>? canDeleteThread = null)
    {
        this.selectionChanged = selectionChanged;
        this.openRecentProject = openRecentProject;
        ToggleChatsCommand = new RelayCommand(() => IsChatsExpanded = !IsChatsExpanded);
        ToggleProjectsCommand = new RelayCommand(() => IsProjectsExpanded = !IsProjectsExpanded);
        ClearChatSearchCommand = new RelayCommand(() => ChatSearchText = string.Empty);
        OpenChatSearchResultCommand = new AsyncRelayCommand(OpenChatSearchResultAsync);
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
        TogglePinThreadCommand = new AsyncRelayCommand(
            togglePinThread ?? (() => Task.CompletedTask),
            canTogglePinThread ?? (() => false));
        DeleteThreadCommand = new AsyncRelayCommand(
            deleteThread ?? (() => Task.CompletedTask),
            canDeleteThread ?? (() => false));
        statefulCommands =
        [
            (AsyncRelayCommand)NewThreadCommand,
            (AsyncRelayCommand)NewGeneralThreadCommand,
            (AsyncRelayCommand)ResumeThreadCommand,
            (AsyncRelayCommand)ForkThreadCommand,
            (AsyncRelayCommand)ArchiveThreadCommand,
            (AsyncRelayCommand)UnarchiveThreadCommand,
            (AsyncRelayCommand)RemoveWorktreeCommand,
            (AsyncRelayCommand)TogglePinThreadCommand,
            (AsyncRelayCommand)DeleteThreadCommand
        ];
    }

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<ProjectThreadState> Threads { get; } = [];

    public ObservableCollection<ProjectThreadState> GeneralThreads { get; } = [];

    public ObservableCollection<ProjectNavigationItemViewModel> Projects { get; } = [];

    public ObservableCollection<ChatSearchResultViewModel> ChatSearchResults { get; } = [];

    public ICommand ToggleChatsCommand { get; }
    public ICommand ToggleProjectsCommand { get; }
    public ICommand ClearChatSearchCommand { get; }
    public ICommand OpenChatSearchResultCommand { get; }
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
    public ICommand TogglePinThreadCommand { get; }
    public ICommand DeleteThreadCommand { get; }

    public IReadOnlyList<string> WorkspaceModeOptions { get; } = ["Current checkout", "New worktree"];

    public bool IsChatsExpanded
    {
        get => isChatsExpanded;
        set
        {
            if (SetProperty(ref isChatsExpanded, value))
            {
                OnPropertyChanged(nameof(ChatsChevron));
            }
        }
    }

    public bool IsProjectsExpanded
    {
        get => isProjectsExpanded;
        set
        {
            if (SetProperty(ref isProjectsExpanded, value))
            {
                OnPropertyChanged(nameof(ProjectsChevron));
            }
        }
    }

    public string ChatsChevron => IsChatsExpanded ? "\u25be" : "\u25b8";

    public string ProjectsChevron => IsProjectsExpanded ? "\u25be" : "\u25b8";

    public string ChatSearchText
    {
        get => chatSearchText;
        set
        {
            if (SetProperty(ref chatSearchText, value ?? string.Empty))
            {
                RefreshChatSearch();
                OnPropertyChanged(nameof(HasChatSearch));
            }
        }
    }

    public bool HasChatSearch => !string.IsNullOrWhiteSpace(ChatSearchText);

    public string ChatSearchSummary => ChatSearchResults.Count == 1
        ? "1 chat found"
        : $"{ChatSearchResults.Count} chats found";

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
            if (ReferenceEquals(selectedThread, value))
            {
                return;
            }

            if (selectedThread is not null)
            {
                selectedThread.PropertyChanged -= OnSelectedThreadPropertyChanged;
            }

            if (SetProperty(ref selectedThread, value))
            {
                if (selectedThread is not null)
                {
                    selectedThread.PropertyChanged += OnSelectedThreadPropertyChanged;
                }
                OnPropertyChanged(nameof(ActiveWorkspacePath));
                OnPropertyChanged(nameof(ActiveWorkspaceLabel));
                OnPropertyChanged(nameof(SelectedThreadTitle));
                OnPropertyChanged(nameof(SelectedGeneralThread));
                OnPropertyChanged(nameof(PinActionLabel));
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

    public string SelectedThreadTitle => SelectedThread?.DisplayTitle ?? "No chat selected";

    public string PinActionLabel => SelectedThread?.IsPinned == true ? "Unpin" : "Pin";

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
        navigationThreads.Clear();
        navigationThreads.AddRange(threadList);
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
        RefreshChatSearch();
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

    private void RefreshChatSearch()
    {
        ChatSearchResults.Clear();
        var query = ChatSearchText.Trim();
        if (query.Length == 0)
        {
            OnPropertyChanged(nameof(ChatSearchSummary));
            return;
        }

        foreach (var thread in navigationThreads
            .Where(thread => TryCreateSearchSnippet(thread, query, out _))
            .OrderByDescending(thread => thread.IsPinned)
            .ThenByDescending(thread => thread.UpdatedAt)
            .Take(50))
        {
            _ = TryCreateSearchSnippet(thread, query, out var snippet);
            ChatSearchResults.Add(new ChatSearchResultViewModel(
                thread,
                ResolveScopeLabel(thread),
                snippet));
        }

        OnPropertyChanged(nameof(ChatSearchSummary));
    }

    private async Task OpenChatSearchResultAsync(object? parameter)
    {
        if (parameter is not ChatSearchResultViewModel result)
        {
            return;
        }

        var thread = result.Thread;
        if (thread.ScopeKind == ThreadScopeKind.General)
        {
            SelectedThread = thread;
        }
        else
        {
            if (!ProjectNavigationItemViewModel.PathsEqual(SelectedProjectPath, thread.ProjectPath))
            {
                await openRecentProject(thread.ProjectPath).ConfigureAwait(true);
            }

            SelectedThread = Threads.FirstOrDefault(candidate =>
                string.Equals(candidate.ThreadId, thread.ThreadId, StringComparison.Ordinal));
        }

        if (SelectedThread is not null)
        {
            ChatSearchText = string.Empty;
        }
    }

    private string ResolveScopeLabel(ProjectThreadState thread)
    {
        if (thread.ScopeKind == ThreadScopeKind.General)
        {
            return "Chats";
        }

        return RecentProjects.FirstOrDefault(project =>
            ProjectNavigationItemViewModel.PathsEqual(project.Path, thread.ProjectPath))?.Name
            ?? new DirectoryInfo(thread.ProjectPath).Name;
    }

    private static bool TryCreateSearchSnippet(ProjectThreadState thread, string query, out string snippet)
    {
        var candidates = new[]
            {
                thread.Title,
                thread.Preview,
                thread.FinalResponse
            }
            .Concat(thread.ConversationTurns.SelectMany(turn =>
                new[] { turn.UserPrompt, turn.AssistantResponse }));
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var matchIndex = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                continue;
            }

            var normalized = string.Join(' ', candidate
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var normalizedMatchIndex = normalized.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, normalizedMatchIndex - 32);
            var length = Math.Min(120, normalized.Length - start);
            snippet = $"{(start > 0 ? "\u2026" : string.Empty)}{normalized.Substring(start, length)}{(start + length < normalized.Length ? "\u2026" : string.Empty)}";
            return true;
        }

        snippet = string.Empty;
        return false;
    }

    private void OnSelectedThreadPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProjectThreadState.IsPinned))
        {
            OnPropertyChanged(nameof(PinActionLabel));
        }
    }
}

public sealed class ChatSearchResultViewModel(
    ProjectThreadState thread,
    string scopeLabel,
    string snippet)
{
    public ProjectThreadState Thread { get; } = thread;

    public string ThreadId => Thread.ThreadId;

    public string Title => Thread.DisplayTitle;

    public string ScopeLabel { get; } = scopeLabel;

    public string Snippet { get; } = snippet;

    public bool IsPinned => Thread.IsPinned;

    public bool IsArchived => Thread.IsArchived;
}
