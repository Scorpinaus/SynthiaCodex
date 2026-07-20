using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.App.ViewModels;

public sealed record GitContext(string? ProjectPath, string? WorkspacePath, bool IsGeneral = false);

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

        var previousPath = SelectedFile?.Path;
        IsBusy = true;
        StatusMessage = "Refreshing Git status";
        try
        {
            var workspace = context.WorkspacePath ?? context.ProjectPath;
            if (!Directory.Exists(workspace))
            {
                Reset($"The active workspace is unavailable: {workspace}");
                return;
            }

            var state = await gitService.GetRepositoryStateAsync(workspace).ConfigureAwait(true);
            ChangedFiles.Clear();
            repositoryRoot = state.RootPath;
            Branch = state.Branch ?? "No repository";
            OnPropertyChanged(nameof(IsRepository));
            if (!state.IsRepository)
            {
                SelectedFile = null;
                StatusMessage = state.ErrorMessage ?? "No Git repository detected";
                return;
            }

            foreach (var file in state.ChangedFiles)
            {
                ChangedFiles.Add(file);
            }

            SelectedFile = ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, previousPath, StringComparison.OrdinalIgnoreCase));
            StatusMessage = ChangedFiles.Count == 0
                ? $"{Branch}: working tree clean"
                : $"{Branch}: {ChangedFiles.Count} changed file{(ChangedFiles.Count == 1 ? string.Empty : "s")}";
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
        repositoryRoot = null;
        Branch = "No repository";
        ChangedFiles.Clear();
        SelectedFile = null;
        StatusMessage = message;
        OnPropertyChanged(nameof(IsRepository));
        RaiseCommandStates();
    }

    private bool CanUseProject() => !isShuttingDown() && !IsBusy && !string.IsNullOrWhiteSpace(contextProvider().ProjectPath);
    private bool CanShowWorkingDiff() => CanMutateSelectedFile() && SelectedFile?.HasWorkingTreeChanges == true;
    private bool CanShowStagedDiff() => CanMutateSelectedFile() && SelectedFile?.IsStaged == true;
    private bool CanStage() => CanMutateSelectedFile() && SelectedFile?.HasWorkingTreeChanges == true;
    private bool CanUnstage() => CanMutateSelectedFile() && SelectedFile?.IsStaged == true;
    private bool CanMutateSelectedFile() => !isShuttingDown() && !IsBusy && IsRepository && SelectedFile is not null;
    private bool CanCommit() => !isShuttingDown() && !IsBusy && IsRepository && !string.IsNullOrWhiteSpace(CommitMessage) && ChangedFiles.Any(file => file.IsStaged);
    private bool CanOpenProjectTarget() => !isShuttingDown() && !string.IsNullOrWhiteSpace(contextProvider().ProjectPath);
}
