using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.App.ViewModels;

public sealed record GitContext(
    string? ProjectPath,
    string? WorkspacePath,
    IReadOnlyList<string>? ProjectFolderPaths = null,
    bool IsGeneral = false);

public sealed record GitRepositoryOption(
    string RootPath,
    string DisplayName,
    string Branch,
    bool IsPrimary,
    GitRepositoryState State)
{
    public string DisplayLabel => IsPrimary
        ? $"{DisplayName} (Primary) · {Branch}"
        : $"{DisplayName} · {Branch}";
}

public sealed class GitViewModel : ObservableObject
{
    private readonly IGitService gitService;
    private readonly IUserInteractionService userInteractionService;
    private readonly IAppLogger logger;
    private readonly Func<GitContext> contextProvider;
    private readonly Func<bool> isShuttingDown;
    private readonly Action<string> reportStatus;
    private readonly AsyncRelayCommand refreshCommand;
    private readonly AsyncRelayCommand showWorkingDiffCommand;
    private readonly AsyncRelayCommand showStagedDiffCommand;
    private readonly AsyncRelayCommand stageCommand;
    private readonly AsyncRelayCommand unstageCommand;
    private readonly AsyncRelayCommand discardCommand;
    private readonly AsyncRelayCommand commitCommand;
    private readonly RelayCommand openEditorCommand;
    private readonly RelayCommand revealExplorerCommand;
    private string? repositoryRoot;
    private string branch = "No repository";
    private string statusMessage = "Select a project to inspect Git changes";
    private string selectedDiff = "Select a changed file to inspect its diff.";
    private string commitMessage = string.Empty;
    private GitChangedFile? selectedFile;
    private GitRepositoryOption? selectedRepository;
    private bool isBusy;
    private bool showingStagedDiff;

    public GitViewModel(
        IGitService gitService,
        IUserInteractionService userInteractionService,
        IAppLogger logger,
        Func<GitContext> contextProvider,
        Func<bool> isShuttingDown,
        Action<string> reportStatus)
    {
        this.gitService = gitService;
        this.userInteractionService = userInteractionService;
        this.logger = logger;
        this.contextProvider = contextProvider;
        this.isShuttingDown = isShuttingDown;
        this.reportStatus = reportStatus;
        RefreshCommand = refreshCommand = new AsyncRelayCommand(RefreshAsync, CanUseProject);
        ShowWorkingDiffCommand = showWorkingDiffCommand = new AsyncRelayCommand(() => LoadDiffAsync(false), CanShowWorkingDiff);
        ShowStagedDiffCommand = showStagedDiffCommand = new AsyncRelayCommand(() => LoadDiffAsync(true), CanShowStagedDiff);
        StageCommand = stageCommand = new AsyncRelayCommand(StageAsync, CanStage);
        UnstageCommand = unstageCommand = new AsyncRelayCommand(UnstageAsync, CanUnstage);
        DiscardCommand = discardCommand = new AsyncRelayCommand(DiscardAsync, CanMutateSelectedFile);
        CommitCommand = commitCommand = new AsyncRelayCommand(CommitAsync, CanCommit);
        OpenEditorCommand = openEditorCommand = new RelayCommand(OpenInEditor, CanOpenProjectTarget);
        RevealExplorerCommand = revealExplorerCommand = new RelayCommand(RevealInExplorer, CanOpenProjectTarget);
    }

    public ObservableCollection<GitChangedFile> ChangedFiles { get; } = [];

    public ObservableCollection<GitRepositoryOption> Repositories { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand ShowWorkingDiffCommand { get; }
    public ICommand ShowStagedDiffCommand { get; }
    public ICommand StageCommand { get; }
    public ICommand UnstageCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand OpenEditorCommand { get; }
    public ICommand RevealExplorerCommand { get; }

    public string Branch
    {
        get => branch;
        private set => SetProperty(ref branch, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsRepository => !string.IsNullOrWhiteSpace(repositoryRoot);

    public bool HasMultipleRepositories => Repositories.Count > 1;

    public GitRepositoryOption? SelectedRepository
    {
        get => selectedRepository;
        set
        {
            if (ReferenceEquals(selectedRepository, value))
            {
                return;
            }

            var previousPath = value is not null && PathsEqual(repositoryRoot, value.RootPath)
                ? SelectedFile?.Path
                : null;
            if (SetProperty(ref selectedRepository, value))
            {
                ApplyRepository(value, previousPath);
            }
        }
    }

    public GitChangedFile? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (!SetProperty(ref selectedFile, value))
            {
                return;
            }

            RaiseCommandStates();
            if (value is null)
            {
                SelectedDiff = "Select a changed file to inspect its diff.";
            }
            else
            {
                _ = LoadDiffAsync(value.IsStaged && !value.HasWorkingTreeChanges);
            }
        }
    }

    public string SelectedDiff
    {
        get => selectedDiff;
        private set => SetProperty(ref selectedDiff, value);
    }

    public string DiffViewLabel => showingStagedDiff ? "Staged diff" : "Working tree diff";

    public string CommitMessage
    {
        get => commitMessage;
        set
        {
            if (SetProperty(ref commitMessage, value))
            {
                commitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public async Task RefreshAsync()
    {
        var context = contextProvider();
        if (string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            Reset(context.IsGeneral
                ? "Chats are not attached to a Git project"
                : "Select a project to inspect Git changes");
            return;
        }

        var previousRoot = SelectedRepository?.RootPath;
        var previousPath = SelectedFile?.Path;
        IsBusy = true;
        StatusMessage = "Refreshing Git status";
        try
        {
            var candidates = GetRepositoryCandidates(context);
            if (candidates.Count == 0)
            {
                Reset("No attached project folders are currently available");
                return;
            }

            var discovered = new List<GitRepositoryOption>();
            string? firstError = null;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                try
                {
                    var state = await gitService.GetRepositoryStateAsync(candidate).ConfigureAwait(true);
                    firstError ??= state.ErrorMessage;
                    if (!state.IsRepository || string.IsNullOrWhiteSpace(state.RootPath) ||
                        discovered.Any(repository => PathsEqual(repository.RootPath, state.RootPath)))
                    {
                        continue;
                    }

                    var root = Path.GetFullPath(state.RootPath);
                    discovered.Add(new GitRepositoryOption(
                        root,
                        GetRepositoryDisplayName(root),
                        state.Branch ?? "Detached HEAD",
                        index == 0,
                        state));
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                    logger.Log(
                        AppLogLevel.Warning,
                        "git_repository_scan_failed",
                        "Could not inspect an attached project folder for Git status.",
                        new Dictionary<string, string?> { ["path"] = candidate },
                        ex);
                }
            }

            if (discovered.Count == 0)
            {
                Reset(firstError ?? "No Git repository detected in the attached project folders");
                return;
            }

            Repositories.Clear();
            foreach (var repository in discovered)
            {
                Repositories.Add(repository);
            }
            OnPropertyChanged(nameof(HasMultipleRepositories));

            var target = Repositories.FirstOrDefault(repository => PathsEqual(repository.RootPath, previousRoot))
                ?? Repositories[0];
            selectedRepository = null;
            SelectedRepository = target;
            if (PathsEqual(target.RootPath, previousRoot) && previousPath is not null)
            {
                SelectedFile = ChangedFiles.FirstOrDefault(file =>
                    string.Equals(file.Path, previousPath, StringComparison.OrdinalIgnoreCase))
                    ?? ChangedFiles.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Reset(ex.Message);
            logger.Log(AppLogLevel.Warning, "git_status_failed", "Could not refresh Git status.", exception: ex);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    public void RaiseCommandStates()
    {
        refreshCommand.RaiseCanExecuteChanged();
        showWorkingDiffCommand.RaiseCanExecuteChanged();
        showStagedDiffCommand.RaiseCanExecuteChanged();
        stageCommand.RaiseCanExecuteChanged();
        unstageCommand.RaiseCanExecuteChanged();
        discardCommand.RaiseCanExecuteChanged();
        commitCommand.RaiseCanExecuteChanged();
        openEditorCommand.RaiseCanExecuteChanged();
        revealExplorerCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadDiffAsync(bool staged)
    {
        if (SelectedFile is null || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        IsBusy = true;
        showingStagedDiff = staged;
        OnPropertyChanged(nameof(DiffViewLabel));
        SelectedDiff = "Loading diff...";
        try
        {
            SelectedDiff = await gitService.GetDiffAsync(repositoryRoot, SelectedFile, staged).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SelectedDiff = ex.Message;
            StatusMessage = "Could not load the selected diff";
            logger.Log(AppLogLevel.Warning, "git_diff_failed", "Could not load a Git diff.", exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task StageAsync()
    {
        var path = SelectedFile!.Path;
        return RunMutationAsync(() => gitService.StageAsync(repositoryRoot!, [path]), $"Staged {path}");
    }

    private Task UnstageAsync()
    {
        var path = SelectedFile!.Path;
        return RunMutationAsync(() => gitService.UnstageAsync(repositoryRoot!, [path]), $"Unstaged {path}");
    }

    private async Task DiscardAsync()
    {
        var file = SelectedFile!;
        var action = file.IsUntracked ? "delete the untracked file" : "discard its staged and working-tree changes";
        if (!userInteractionService.ConfirmDestructiveAction(
                "Discard Git changes",
                $"This will {action}:\n\n{file.DisplayPath}\n\nThis cannot be undone. Continue?"))
        {
            StatusMessage = "Discard cancelled";
            return;
        }

        await RunMutationAsync(() => gitService.RevertAsync(repositoryRoot!, [file]), $"Discarded changes to {file.Path}").ConfigureAwait(true);
    }

    private async Task CommitAsync()
    {
        IsBusy = true;
        try
        {
            var result = await gitService.CommitAsync(repositoryRoot!, CommitMessage).ConfigureAwait(true);
            CommitMessage = string.Empty;
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = $"Committed {result.CommitId}: {result.Summary}";
            reportStatus($"Git commit {result.CommitId} created");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "git_commit_failed", "Could not create a Git commit.", exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunMutationAsync(Func<Task> operation, string successMessage)
    {
        IsBusy = true;
        try
        {
            await operation().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "git_mutation_failed", "A Git operation failed.", exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenInEditor()
    {
        try
        {
            userInteractionService.OpenInEditor(GetSelectedTargetPath());
            StatusMessage = "Opened in editor";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void RevealInExplorer()
    {
        try
        {
            userInteractionService.RevealInExplorer(GetSelectedTargetPath());
            StatusMessage = "Opened in Explorer";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private string GetSelectedTargetPath()
    {
        var root = repositoryRoot ?? contextProvider().ProjectPath ?? throw new InvalidOperationException("Select a project first.");
        return SelectedFile is null
            ? root
            : Path.GetFullPath(Path.Combine(root, SelectedFile.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private void Reset(string message)
    {
        Repositories.Clear();
        selectedRepository = null;
        OnPropertyChanged(nameof(SelectedRepository));
        OnPropertyChanged(nameof(HasMultipleRepositories));
        repositoryRoot = null;
        Branch = "No repository";
        ChangedFiles.Clear();
        SelectedFile = null;
        StatusMessage = message;
        OnPropertyChanged(nameof(IsRepository));
        RaiseCommandStates();
    }

    private void ApplyRepository(GitRepositoryOption? repository, string? preferredFilePath)
    {
        repositoryRoot = repository?.RootPath;
        Branch = repository?.Branch ?? "No repository";
        ChangedFiles.Clear();
        if (repository is not null)
        {
            foreach (var file in repository.State.ChangedFiles)
            {
                ChangedFiles.Add(file);
            }
        }

        SelectedFile = ChangedFiles.FirstOrDefault(file =>
                string.Equals(file.Path, preferredFilePath, StringComparison.OrdinalIgnoreCase))
            ?? ChangedFiles.FirstOrDefault();
        SelectedDiff = SelectedFile is null
            ? "Select a changed file to inspect its diff."
            : SelectedDiff;
        StatusMessage = repository is null
            ? "No Git repository detected"
            : ChangedFiles.Count == 0
                ? $"{repository.DisplayName} · {Branch}: working tree clean"
                : $"{repository.DisplayName} · {Branch}: {ChangedFiles.Count} changed file{(ChangedFiles.Count == 1 ? string.Empty : "s")}";
        OnPropertyChanged(nameof(IsRepository));
        RaiseCommandStates();
    }

    private static IReadOnlyList<string> GetRepositoryCandidates(GitContext context)
    {
        var candidates = new List<string>();
        void Add(string? path, bool requireExists = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = Path.GetFullPath(path);
            if ((!requireExists || Directory.Exists(normalized)) &&
                !candidates.Any(candidate => PathsEqual(candidate, normalized)))
            {
                candidates.Add(normalized);
            }
        }

        var projectRoots = context.ProjectFolderPaths is { Count: > 0 }
            ? context.ProjectFolderPaths
            : [context.ProjectPath!];
        var workspaceIsAttached = projectRoots.Any(path => PathsEqual(path, context.WorkspacePath));
        if (!workspaceIsAttached)
        {
            Add(context.WorkspacePath);
        }
        foreach (var root in projectRoots)
        {
            Add(root);
        }
        return candidates;
    }

    private static string GetRepositoryDisplayName(string root)
    {
        var name = new DirectoryInfo(root).Name;
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private bool CanUseProject() => !isShuttingDown() && !IsBusy && !string.IsNullOrWhiteSpace(contextProvider().ProjectPath);
    private bool CanShowWorkingDiff() => CanMutateSelectedFile() && SelectedFile?.HasWorkingTreeChanges == true;
    private bool CanShowStagedDiff() => CanMutateSelectedFile() && SelectedFile?.IsStaged == true;
    private bool CanStage() => CanMutateSelectedFile() && SelectedFile?.HasWorkingTreeChanges == true;
    private bool CanUnstage() => CanMutateSelectedFile() && SelectedFile?.IsStaged == true;
    private bool CanMutateSelectedFile() => !isShuttingDown() && !IsBusy && IsRepository && SelectedFile is not null;
    private bool CanCommit() => !isShuttingDown() && !IsBusy && IsRepository && !string.IsNullOrWhiteSpace(CommitMessage) && ChangedFiles.Any(file => file.IsStaged);
    private bool CanOpenProjectTarget() => !isShuttingDown() && !string.IsNullOrWhiteSpace(contextProvider().ProjectPath);
}
