using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;
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

public sealed record GitDiffScopeOption(GitDiffScope Scope, string DisplayName);

public sealed class GitDiffLineViewModel(
    GitDiffLine line,
    GitDiffHunkPatch? hunkPatch = null,
    bool canStageHunk = false,
    bool canUnstageHunk = false,
    bool canDiscardHunk = false) : ObservableObject
{
    private bool isCommentEditorOpen;
    private string commentDraft = string.Empty;

    public GitDiffLineKind Kind => line.Kind;

    public int? OldLineNumber => line.OldLineNumber;

    public int? NewLineNumber => line.NewLineNumber;

    public string OldLineDisplay => line.OldLineDisplay;

    public string NewLineDisplay => line.NewLineDisplay;

    public string Prefix => line.Prefix;

    public string Content => line.Content;

    public GitDiffHunkPatch? HunkPatch => hunkPatch;

    public bool CanStageHunk => canStageHunk;

    public bool CanUnstageHunk => canUnstageHunk;

    public bool CanDiscardHunk => canDiscardHunk;

    public string AutomationName => string.IsNullOrWhiteSpace(line.Text)
        ? "Blank diff line"
        : $"Diff line {line.Text}";

    public bool CanAddComment => Kind is GitDiffLineKind.Context or GitDiffLineKind.Addition or GitDiffLineKind.Removal &&
        (OldLineNumber.HasValue || NewLineNumber.HasValue);

    public bool IsCommentEditorOpen
    {
        get => isCommentEditorOpen;
        internal set => SetProperty(ref isCommentEditorOpen, value);
    }

    public string CommentDraft
    {
        get => commentDraft;
        set => SetProperty(ref commentDraft, value ?? string.Empty);
    }

    public ObservableCollection<CodexReviewFinding> ReviewFindings { get; } = [];

    public ObservableCollection<GitInlineCommentViewModel> UserComments { get; } = [];
}

public sealed class GitInlineCommentViewModel : ObservableObject
{
    private GitInlineComment comment;
    private bool isEditing;
    private string editText;

    public GitInlineCommentViewModel(GitInlineComment comment)
    {
        this.comment = comment.Clone();
        editText = this.comment.Body;
    }

    public string Id => comment.Id;

    public string RepositoryRoot => comment.RepositoryRoot;

    public string FilePath => comment.FilePath;

    public string? OriginalFilePath => comment.OriginalFilePath;

    public GitDiffSide Side => comment.Side;

    public int LineNumber => comment.LineNumber;

    public string LineText => comment.LineText;

    public string Body => comment.Body;

    public string SideLabel => comment.SideLabel;

    public string DisplayLocation => comment.DisplayLocation;

    public string AutomationName => comment.AutomationName;

    public bool IsEditing
    {
        get => isEditing;
        internal set => SetProperty(ref isEditing, value);
    }

    public string EditText
    {
        get => editText;
        set => SetProperty(ref editText, value ?? string.Empty);
    }

    public GitInlineComment Snapshot() => comment.Clone();

    internal void BeginEdit()
    {
        EditText = Body;
        IsEditing = true;
    }

    internal void CancelEdit()
    {
        EditText = Body;
        IsEditing = false;
    }

    internal void Replace(GitInlineComment value)
    {
        comment = value.Clone();
        EditText = comment.Body;
        IsEditing = false;
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(AutomationName));
    }
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
    private readonly AsyncRelayCommand stageHunkCommand;
    private readonly AsyncRelayCommand unstageHunkCommand;
    private readonly AsyncRelayCommand discardHunkCommand;
    private readonly AsyncRelayCommand commitCommand;
    private readonly RelayCommand openEditorCommand;
    private readonly RelayCommand revealExplorerCommand;
    private readonly RelayCommand beginAddCommentCommand;
    private readonly RelayCommand cancelAddCommentCommand;
    private readonly RelayCommand saveCommentCommand;
    private readonly RelayCommand beginEditCommentCommand;
    private readonly RelayCommand cancelEditCommentCommand;
    private readonly RelayCommand saveEditedCommentCommand;
    private readonly RelayCommand removeCommentCommand;
    private string? repositoryRoot;
    private string branch = "No repository";
    private string statusMessage = "Select a project to inspect Git changes";
    private string selectedDiff = "Select a changed file to inspect its diff.";
    private string commitMessage = string.Empty;
    private GitChangedFile? selectedFile;
    private GitRepositoryOption? selectedRepository;
    private GitDiffScopeOption selectedDiffScope;
    private GitReviewCommit? selectedReviewCommit;
    private string? selectedReviewBranch;
    private bool isBusy;
    private bool diffScopeInitialized;
    private string lastTurnDiff = string.Empty;
    private readonly Dictionary<GitChangedFile, string> comparisonDiffs = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<CodexReviewFinding> reviewFindings = [];
    private long diffLoadVersion;

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
        DiffScopes =
        [
            new(GitDiffScope.Unstaged, "Unstaged"),
            new(GitDiffScope.Staged, "Staged"),
            new(GitDiffScope.Commit, "Commit"),
            new(GitDiffScope.Branch, "Branch"),
            new(GitDiffScope.LastTurn, "Last turn")
        ];
        selectedDiffScope = DiffScopes[0];
        RefreshCommand = refreshCommand = new AsyncRelayCommand(RefreshAsync, CanUseProject);
        ShowWorkingDiffCommand = showWorkingDiffCommand = new AsyncRelayCommand(
            () => SwitchScopeAsync(GitDiffScope.Unstaged),
            CanShowWorkingDiff);
        ShowStagedDiffCommand = showStagedDiffCommand = new AsyncRelayCommand(
            () => SwitchScopeAsync(GitDiffScope.Staged),
            CanShowStagedDiff);
        StageCommand = stageCommand = new AsyncRelayCommand(StageAsync, CanStage);
        UnstageCommand = unstageCommand = new AsyncRelayCommand(UnstageAsync, CanUnstage);
        DiscardCommand = discardCommand = new AsyncRelayCommand(DiscardAsync, CanMutateSelectedFile);
        StageHunkCommand = stageHunkCommand = new AsyncRelayCommand(
            parameter => ApplyHunkAsync(parameter, GitHunkOperation.Stage),
            CanStageHunk);
        UnstageHunkCommand = unstageHunkCommand = new AsyncRelayCommand(
            parameter => ApplyHunkAsync(parameter, GitHunkOperation.Unstage),
            CanUnstageHunk);
        DiscardHunkCommand = discardHunkCommand = new AsyncRelayCommand(DiscardHunkAsync, CanDiscardHunk);
        CommitCommand = commitCommand = new AsyncRelayCommand(CommitAsync, CanCommit);
        OpenEditorCommand = openEditorCommand = new RelayCommand(OpenInEditor, CanOpenProjectTarget);
        RevealExplorerCommand = revealExplorerCommand = new RelayCommand(RevealInExplorer, CanOpenProjectTarget);
        BeginAddCommentCommand = beginAddCommentCommand = new RelayCommand(BeginAddComment, CanBeginAddComment);
        CancelAddCommentCommand = cancelAddCommentCommand = new RelayCommand(CancelAddComment, CanCancelAddComment);
        SaveCommentCommand = saveCommentCommand = new RelayCommand(SaveComment, CanSaveComment);
        BeginEditCommentCommand = beginEditCommentCommand = new RelayCommand(BeginEditComment, CanBeginEditComment);
        CancelEditCommentCommand = cancelEditCommentCommand = new RelayCommand(CancelEditComment, CanCancelEditComment);
        SaveEditedCommentCommand = saveEditedCommentCommand = new RelayCommand(SaveEditedComment, CanSaveEditedComment);
        RemoveCommentCommand = removeCommentCommand = new RelayCommand(RemoveComment, CanRemoveComment);
    }

    public ObservableCollection<GitChangedFile> ChangedFiles { get; } = [];

    public ObservableCollection<GitRepositoryOption> Repositories { get; } = [];

    public IReadOnlyList<GitDiffScopeOption> DiffScopes { get; }

    public ObservableCollection<string> ReviewBranches { get; } = [];

    public ObservableCollection<GitReviewCommit> ReviewCommits { get; } = [];

    public ObservableCollection<GitDiffLineViewModel> SelectedDiffLines { get; } = [];

    public ObservableCollection<CodexReviewFinding> UnmatchedReviewFindings { get; } = [];

    public ObservableCollection<GitInlineCommentViewModel> ReviewComments { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand ShowWorkingDiffCommand { get; }
    public ICommand ShowStagedDiffCommand { get; }
    public ICommand StageCommand { get; }
    public ICommand UnstageCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand StageHunkCommand { get; }
    public ICommand UnstageHunkCommand { get; }
    public ICommand DiscardHunkCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand OpenEditorCommand { get; }
    public ICommand RevealExplorerCommand { get; }
    public ICommand BeginAddCommentCommand { get; }
    public ICommand CancelAddCommentCommand { get; }
    public ICommand SaveCommentCommand { get; }
    public ICommand BeginEditCommentCommand { get; }
    public ICommand CancelEditCommentCommand { get; }
    public ICommand SaveEditedCommentCommand { get; }
    public ICommand RemoveCommentCommand { get; }

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

    public bool ShowsRepositorySelector => SelectedDiffScope.Scope != GitDiffScope.LastTurn;

    public bool ShowsBranchSelector => SelectedDiffScope.Scope == GitDiffScope.Branch;

    public bool ShowsCommitSelector => SelectedDiffScope.Scope == GitDiffScope.Commit;

    public bool IsHistoricalScope => SelectedDiffScope.Scope is GitDiffScope.Commit or GitDiffScope.Branch or GitDiffScope.LastTurn;

    public GitDiffScopeOption SelectedDiffScope
    {
        get => selectedDiffScope;
        set
        {
            if (value is null || !SetProperty(ref selectedDiffScope, value))
            {
                return;
            }
            diffScopeInitialized = true;
            OnPropertyChanged(nameof(ShowsRepositorySelector));
            OnPropertyChanged(nameof(ShowsBranchSelector));
            OnPropertyChanged(nameof(ShowsCommitSelector));
            OnPropertyChanged(nameof(IsHistoricalScope));
            OnPropertyChanged(nameof(DiffViewLabel));
            RaiseCommandStates();
            _ = LoadSelectedScopeAsync(SelectedFile?.Path);
        }
    }

    public string? SelectedReviewBranch
    {
        get => selectedReviewBranch;
        set
        {
            if (SetProperty(ref selectedReviewBranch, value) && ShowsBranchSelector)
            {
                _ = LoadSelectedScopeAsync(SelectedFile?.Path);
            }
        }
    }

    public GitReviewCommit? SelectedReviewCommit
    {
        get => selectedReviewCommit;
        set
        {
            if (SetProperty(ref selectedReviewCommit, value) && ShowsCommitSelector)
            {
                _ = LoadSelectedScopeAsync(SelectedFile?.Path);
            }
        }
    }

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
            else if (comparisonDiffs.TryGetValue(value, out var comparisonDiff))
            {
                SelectedDiff = comparisonDiff;
            }
            else if (SelectedDiffScope.Scope is GitDiffScope.Unstaged or GitDiffScope.Staged)
            {
                _ = LoadDiffAsync(SelectedDiffScope.Scope == GitDiffScope.Staged);
            }
            else
            {
                SelectedDiff = "No diff is available for the selected file.";
            }
        }
    }

    public string SelectedDiff
    {
        get => selectedDiff;
        private set
        {
            if (SetProperty(ref selectedDiff, value))
            {
                RebuildDiffProjection();
            }
        }
    }

    public string DiffViewLabel => SelectedDiffScope.Scope switch
    {
        GitDiffScope.Unstaged => "Unstaged diff",
        GitDiffScope.Staged => "Staged diff",
        GitDiffScope.Commit => "Commit diff",
        GitDiffScope.Branch => "Branch diff",
        GitDiffScope.LastTurn => "Last turn diff",
        _ => "Diff"
    };

    public bool HasUnmatchedReviewFindings => UnmatchedReviewFindings.Count > 0;

    public bool HasReviewComments => ReviewComments.Count > 0;

    public string PendingReviewCommentsLabel => ReviewComments.Count == 1
        ? "1 pending inline comment will accompany your next message"
        : $"{ReviewComments.Count} pending inline comments will accompany your next message";

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

    public void SetReviewFindings(IEnumerable<CodexReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        reviewFindings = findings.Take(100).ToArray();
        RebuildDiffProjection();
    }

    public void SetLastTurnDiff(string? diff)
    {
        lastTurnDiff = diff?.Length <= CodexConversationTurn.MaximumDiffCharacters ? diff : string.Empty;
        if (SelectedDiffScope.Scope == GitDiffScope.LastTurn)
        {
            _ = LoadSelectedScopeAsync(SelectedFile?.Path);
        }
    }

    public IReadOnlyList<GitInlineComment> CaptureReviewComments() =>
        ReviewComments.Select(item => item.Snapshot()).ToArray();

    public void SetReviewComments(IEnumerable<GitInlineComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ReviewComments.Clear();
        foreach (var comment in GitInlineComment.NormalizeRestored(comments))
        {
            ReviewComments.Add(new GitInlineCommentViewModel(comment));
        }
        RebuildDiffProjection();
        NotifyReviewCommentsChanged();
    }

    public void RemoveReviewComments(IEnumerable<string> commentIds)
    {
        ArgumentNullException.ThrowIfNull(commentIds);
        var ids = commentIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return;
        }

        var removed = false;
        for (var index = ReviewComments.Count - 1; index >= 0; index--)
        {
            if (ids.Contains(ReviewComments[index].Id))
            {
                ReviewComments.RemoveAt(index);
                removed = true;
            }
        }
        if (removed)
        {
            RebuildDiffProjection();
            NotifyReviewCommentsChanged();
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
        stageHunkCommand.RaiseCanExecuteChanged();
        unstageHunkCommand.RaiseCanExecuteChanged();
        discardHunkCommand.RaiseCanExecuteChanged();
        commitCommand.RaiseCanExecuteChanged();
        openEditorCommand.RaiseCanExecuteChanged();
        revealExplorerCommand.RaiseCanExecuteChanged();
        beginAddCommentCommand.RaiseCanExecuteChanged();
        cancelAddCommentCommand.RaiseCanExecuteChanged();
        saveCommentCommand.RaiseCanExecuteChanged();
        beginEditCommentCommand.RaiseCanExecuteChanged();
        cancelEditCommentCommand.RaiseCanExecuteChanged();
        saveEditedCommentCommand.RaiseCanExecuteChanged();
        removeCommentCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadDiffAsync(bool staged)
    {
        if (SelectedFile is null || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        var requestedFile = SelectedFile;
        var requestedRoot = repositoryRoot;
        var requestVersion = ++diffLoadVersion;
        IsBusy = true;
        SelectedDiff = "Loading diff...";
        try
        {
            var diff = await gitService.GetDiffAsync(requestedRoot, requestedFile, staged).ConfigureAwait(true);
            if (requestVersion == diffLoadVersion &&
                ReferenceEquals(SelectedFile, requestedFile) &&
                PathsEqual(repositoryRoot, requestedRoot))
            {
                SelectedDiff = diff;
            }
        }
        catch (Exception ex)
        {
            if (requestVersion == diffLoadVersion)
            {
                SelectedDiff = ex.Message;
                StatusMessage = "Could not load the selected diff";
                logger.Log(AppLogLevel.Warning, "git_diff_failed", "Could not load a Git diff.", exception: ex);
            }
        }
        finally
        {
            if (requestVersion == diffLoadVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task SwitchScopeAsync(GitDiffScope scope)
    {
        var option = DiffScopes.Single(item => item.Scope == scope);
        if (!ReferenceEquals(selectedDiffScope, option))
        {
            selectedDiffScope = option;
            diffScopeInitialized = true;
            OnPropertyChanged(nameof(SelectedDiffScope));
            OnPropertyChanged(nameof(ShowsRepositorySelector));
            OnPropertyChanged(nameof(ShowsBranchSelector));
            OnPropertyChanged(nameof(ShowsCommitSelector));
            OnPropertyChanged(nameof(IsHistoricalScope));
            OnPropertyChanged(nameof(DiffViewLabel));
        }
        RaiseCommandStates();
        await LoadSelectedScopeAsync(SelectedFile?.Path).ConfigureAwait(true);
    }

    private async Task LoadSelectedScopeAsync(string? preferredFilePath)
    {
        var scope = SelectedDiffScope.Scope;
        var requestVersion = ++diffLoadVersion;
        comparisonDiffs.Clear();

        if (scope is GitDiffScope.Unstaged or GitDiffScope.Staged)
        {
            ApplyCurrentScope(preferredFilePath);
            if (SelectedFile is not null)
            {
                await LoadDiffAsync(SelectedDiffScope.Scope == GitDiffScope.Staged).ConfigureAwait(true);
            }
            return;
        }

        if (scope == GitDiffScope.LastTurn)
        {
            ApplyComparisonDocuments(
                GitUnifiedDiffDocumentParser.Parse(lastTurnDiff, "Last turn"),
                preferredFilePath,
                string.IsNullOrWhiteSpace(lastTurnDiff)
                    ? "The latest turn made no file changes"
                    : "Changes from the latest turn");
            return;
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            ApplyComparisonDocuments([], preferredFilePath, "Select a repository to compare");
            return;
        }

        var requestedRoot = repositoryRoot;
        IsBusy = true;
        StatusMessage = scope == GitDiffScope.Commit ? "Loading commit history" : "Loading branches";
        try
        {
            var catalog = await gitService.GetReviewCatalogAsync(requestedRoot).ConfigureAwait(true);
            if (requestVersion != diffLoadVersion || !PathsEqual(repositoryRoot, requestedRoot))
            {
                return;
            }
            ApplyReviewCatalog(catalog);

            var target = scope switch
            {
                GitDiffScope.Commit when SelectedReviewCommit is not null =>
                    GitComparisonTarget.Commit(SelectedReviewCommit.Sha),
                GitDiffScope.Branch when !string.IsNullOrWhiteSpace(SelectedReviewBranch) =>
                    GitComparisonTarget.Branch(SelectedReviewBranch),
                GitDiffScope.Commit => throw new InvalidOperationException("No commits are available to compare."),
                _ => throw new InvalidOperationException("No base branches are available to compare.")
            };
            var documents = await gitService.GetComparisonDiffAsync(requestedRoot, target).ConfigureAwait(true);
            if (requestVersion != diffLoadVersion || !PathsEqual(repositoryRoot, requestedRoot))
            {
                return;
            }
            var label = scope == GitDiffScope.Commit
                ? $"Commit {SelectedReviewCommit!.ShortSha}"
                : $"Changes from {SelectedReviewBranch} merge base to HEAD";
            ApplyComparisonDocuments(documents, preferredFilePath, label);
        }
        catch (Exception ex)
        {
            if (requestVersion == diffLoadVersion)
            {
                ApplyComparisonDocuments([], preferredFilePath, ex.Message);
                logger.Log(AppLogLevel.Warning, "git_comparison_failed", "Could not load a Git comparison.", exception: ex);
            }
        }
        finally
        {
            if (requestVersion == diffLoadVersion)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyReviewCatalog(GitReviewCatalog catalog)
    {
        ReviewBranches.Clear();
        foreach (var branchName in catalog.BaseBranches)
        {
            ReviewBranches.Add(branchName);
        }
        if (!ReviewBranches.Contains(selectedReviewBranch ?? string.Empty, StringComparer.Ordinal))
        {
            selectedReviewBranch = ReviewBranches.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedReviewBranch));
        }

        ReviewCommits.Clear();
        foreach (var commit in catalog.Commits)
        {
            ReviewCommits.Add(commit);
        }
        if (selectedReviewCommit is null || !ReviewCommits.Any(commit => commit.Sha == selectedReviewCommit.Sha))
        {
            selectedReviewCommit = ReviewCommits.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedReviewCommit));
        }
    }

    private void ApplyCurrentScope(string? preferredFilePath)
    {
        ChangedFiles.Clear();
        var files = SelectedRepository?.State.ChangedFiles ?? [];
        if (!diffScopeInitialized &&
            SelectedDiffScope.Scope == GitDiffScope.Unstaged &&
            !files.Any(file => file.HasWorkingTreeChanges) &&
            files.Any(file => file.IsStaged))
        {
            selectedDiffScope = DiffScopes.Single(scope => scope.Scope == GitDiffScope.Staged);
            OnPropertyChanged(nameof(SelectedDiffScope));
            OnPropertyChanged(nameof(DiffViewLabel));
        }
        diffScopeInitialized = true;
        var staged = SelectedDiffScope.Scope == GitDiffScope.Staged;
        foreach (var file in files.Where(file => staged ? file.IsStaged : file.HasWorkingTreeChanges))
        {
            ChangedFiles.Add(file);
        }
        SelectPreferredFile(preferredFilePath);
        StatusMessage = ChangedFiles.Count == 0
            ? staged ? "No staged changes" : "Working tree clean"
            : $"{ChangedFiles.Count} {(staged ? "staged" : "unstaged")} file{(ChangedFiles.Count == 1 ? string.Empty : "s")}";
    }

    private void ApplyComparisonDocuments(
        IReadOnlyList<GitDiffDocument> documents,
        string? preferredFilePath,
        string status)
    {
        ChangedFiles.Clear();
        comparisonDiffs.Clear();
        foreach (var document in documents)
        {
            ChangedFiles.Add(document.File);
            comparisonDiffs[document.File] = document.Diff;
        }
        SelectPreferredFile(preferredFilePath);
        if (ChangedFiles.Count == 0)
        {
            SelectedDiff = status;
        }
        StatusMessage = status;
        RaiseCommandStates();
    }

    private void SelectPreferredFile(string? preferredFilePath)
    {
        SelectedFile = ChangedFiles.FirstOrDefault(file =>
                string.Equals(file.Path, preferredFilePath, StringComparison.OrdinalIgnoreCase))
            ?? ChangedFiles.FirstOrDefault();
        if (SelectedFile is null)
        {
            SelectedDiff = "Select a changed file to inspect its diff.";
        }
    }

    private void RebuildDiffProjection()
    {
        SelectedDiffLines.Clear();
        UnmatchedReviewFindings.Clear();

        var hunkPatches = new Queue<GitDiffHunkPatch>(GitUnifiedDiffParser.ParseHunks(SelectedDiff));
        var supportsWorkingHunks = SelectedFile is
        {
            OriginalPath: null,
            IsUntracked: false,
            WorkTreeStatus: 'M'
        } && SelectedDiffScope.Scope == GitDiffScope.Unstaged;
        var supportsStagedHunks = SelectedFile is
        {
            OriginalPath: null,
            IsUntracked: false,
            IndexStatus: 'M'
        } && SelectedDiffScope.Scope == GitDiffScope.Staged;
        var rows = GitUnifiedDiffParser.Parse(SelectedDiff)
            .Select(line =>
            {
                var patch = line.Kind == GitDiffLineKind.Hunk && hunkPatches.TryDequeue(out var nextPatch)
                    ? nextPatch
                    : null;
                return new GitDiffLineViewModel(
                    line,
                    patch,
                    canStageHunk: patch is not null && supportsWorkingHunks,
                    canUnstageHunk: patch is not null && supportsStagedHunks,
                    canDiscardHunk: patch is not null && supportsWorkingHunks);
            })
            .ToList();
        foreach (var row in rows)
        {
            SelectedDiffLines.Add(row);
        }

        if (SelectedFile is not null && !string.IsNullOrWhiteSpace(repositoryRoot))
        {
            foreach (var finding in reviewFindings.Where(FindingMatchesSelectedFile))
            {
                var anchor = rows
                    .Where(row => row.NewLineNumber is { } line &&
                        line >= finding.StartLine &&
                        line <= finding.EndLine)
                    .LastOrDefault()
                    ?? rows
                        .Where(row => row.OldLineNumber is { } line &&
                            line >= finding.StartLine &&
                            line <= finding.EndLine)
                        .LastOrDefault();
                if (anchor is null)
                {
                    UnmatchedReviewFindings.Add(finding);
                }
                else
                {
                    anchor.ReviewFindings.Add(finding);
                }
            }

            foreach (var comment in ReviewComments.Where(CommentMatchesSelectedFile))
            {
                var anchor = rows.LastOrDefault(row => comment.Side == GitDiffSide.New
                    ? row.NewLineNumber == comment.LineNumber
                    : row.OldLineNumber == comment.LineNumber);
                anchor?.UserComments.Add(comment);
            }
        }

        OnPropertyChanged(nameof(SelectedDiffLines));
        OnPropertyChanged(nameof(UnmatchedReviewFindings));
        OnPropertyChanged(nameof(HasUnmatchedReviewFindings));
    }

    private void BeginAddComment(object? parameter)
    {
        if (parameter is not GitDiffLineViewModel row || !CanBeginAddComment(parameter))
        {
            return;
        }
        foreach (var other in SelectedDiffLines.Where(item => !ReferenceEquals(item, row)))
        {
            other.IsCommentEditorOpen = false;
            other.CommentDraft = string.Empty;
        }
        row.CommentDraft = string.Empty;
        row.IsCommentEditorOpen = true;
        RaiseCommentCommandStates();
    }

    private void CancelAddComment(object? parameter)
    {
        if (parameter is not GitDiffLineViewModel row)
        {
            return;
        }
        row.CommentDraft = string.Empty;
        row.IsCommentEditorOpen = false;
        RaiseCommentCommandStates();
    }

    private void SaveComment(object? parameter)
    {
        if (parameter is not GitDiffLineViewModel row ||
            SelectedFile is null ||
            string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        try
        {
            var side = row.Kind == GitDiffLineKind.Removal ? GitDiffSide.Old : GitDiffSide.New;
            var lineNumber = side == GitDiffSide.Old ? row.OldLineNumber : row.NewLineNumber;
            if (lineNumber is null)
            {
                throw new InvalidDataException("This diff row does not have a commentable line number.");
            }
            var comment = GitInlineComment.Create(
                repositoryRoot,
                SelectedFile.Path,
                SelectedFile.OriginalPath,
                side,
                lineNumber.Value,
                row.Content,
                row.CommentDraft);
            GitInlineComment.NormalizeForSubmission(CaptureReviewComments().Append(comment));
            var viewModel = new GitInlineCommentViewModel(comment);
            ReviewComments.Add(viewModel);
            row.UserComments.Add(viewModel);
            row.CommentDraft = string.Empty;
            row.IsCommentEditorOpen = false;
            StatusMessage = $"Added inline comment at {comment.DisplayLocation}";
            NotifyReviewCommentsChanged();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or NotSupportedException or PathTooLongException)
        {
            StatusMessage = exception.Message;
        }
    }

    private void BeginEditComment(object? parameter)
    {
        if (parameter is not GitInlineCommentViewModel comment || !ReviewComments.Contains(comment))
        {
            return;
        }
        foreach (var other in ReviewComments.Where(item => !ReferenceEquals(item, comment)))
        {
            other.CancelEdit();
        }
        comment.BeginEdit();
        RaiseCommentCommandStates();
    }

    private void CancelEditComment(object? parameter)
    {
        if (parameter is GitInlineCommentViewModel comment && ReviewComments.Contains(comment))
        {
            comment.CancelEdit();
            RaiseCommentCommandStates();
        }
    }

    private void SaveEditedComment(object? parameter)
    {
        if (parameter is not GitInlineCommentViewModel comment || !ReviewComments.Contains(comment))
        {
            return;
        }
        try
        {
            var updated = comment.Snapshot().WithBody(comment.EditText);
            GitInlineComment.NormalizeForSubmission(
                ReviewComments.Where(item => !ReferenceEquals(item, comment)).Select(item => item.Snapshot()).Append(updated));
            comment.Replace(updated);
            RebuildDiffProjection();
            StatusMessage = $"Updated inline comment at {comment.DisplayLocation}";
            NotifyReviewCommentsChanged();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            StatusMessage = exception.Message;
        }
    }

    private void RemoveComment(object? parameter)
    {
        if (parameter is GitInlineCommentViewModel comment && ReviewComments.Remove(comment))
        {
            RebuildDiffProjection();
            StatusMessage = $"Removed inline comment at {comment.DisplayLocation}";
            NotifyReviewCommentsChanged();
        }
    }

    private void NotifyReviewCommentsChanged()
    {
        OnPropertyChanged(nameof(ReviewComments));
        OnPropertyChanged(nameof(HasReviewComments));
        OnPropertyChanged(nameof(PendingReviewCommentsLabel));
        RaiseCommentCommandStates();
    }

    private void RaiseCommentCommandStates()
    {
        beginAddCommentCommand.RaiseCanExecuteChanged();
        cancelAddCommentCommand.RaiseCanExecuteChanged();
        saveCommentCommand.RaiseCanExecuteChanged();
        beginEditCommentCommand.RaiseCanExecuteChanged();
        cancelEditCommentCommand.RaiseCanExecuteChanged();
        saveEditedCommentCommand.RaiseCanExecuteChanged();
        removeCommentCommand.RaiseCanExecuteChanged();
    }

    private bool CommentMatchesSelectedFile(GitInlineCommentViewModel comment) =>
        SelectedFile is not null &&
        !string.IsNullOrWhiteSpace(repositoryRoot) &&
        PathsEqual(comment.RepositoryRoot, repositoryRoot) &&
        (string.Equals(NormalizeRelativePath(comment.FilePath), NormalizeRelativePath(SelectedFile.Path), StringComparison.OrdinalIgnoreCase) ||
         (!string.IsNullOrWhiteSpace(SelectedFile.OriginalPath) &&
          (string.Equals(NormalizeRelativePath(comment.FilePath), NormalizeRelativePath(SelectedFile.OriginalPath), StringComparison.OrdinalIgnoreCase) ||
           string.Equals(NormalizeRelativePath(comment.OriginalFilePath ?? string.Empty), NormalizeRelativePath(SelectedFile.OriginalPath), StringComparison.OrdinalIgnoreCase))));

    private bool FindingMatchesSelectedFile(CodexReviewFinding finding) =>
        SelectedFile is not null &&
        !string.IsNullOrWhiteSpace(repositoryRoot) &&
        (FindingMatchesPath(finding.AbsoluteFilePath, repositoryRoot, SelectedFile.Path) ||
         (!string.IsNullOrWhiteSpace(SelectedFile.OriginalPath) &&
          FindingMatchesPath(finding.AbsoluteFilePath, repositoryRoot, SelectedFile.OriginalPath)));

    private static bool FindingMatchesPath(string findingPath, string root, string relativePath)
    {
        try
        {
            var normalizedFinding = findingPath.Trim().Replace('\\', '/');
            var normalizedRelative = NormalizeRelativePath(relativePath);
            var isDriveAbsolute = normalizedFinding.Length >= 3 &&
                char.IsAsciiLetter(normalizedFinding[0]) &&
                normalizedFinding[1] == ':' &&
                normalizedFinding[2] == '/';
            var isUncAbsolute = normalizedFinding.StartsWith("//", StringComparison.Ordinal);
            if (isDriveAbsolute || isUncAbsolute)
            {
                var expected = Path.GetFullPath(Path.Combine(
                    root,
                    normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
                var actual = Path.GetFullPath(findingPath.Replace('/', Path.DirectorySeparatorChar));
                return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            }

            if (normalizedFinding.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(
                NormalizeRelativePath(normalizedFinding),
                normalizedRelative,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (normalized.StartsWith("a/", StringComparison.Ordinal) ||
            normalized.StartsWith("b/", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        return normalized;
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

    private Task ApplyHunkAsync(object? parameter, GitHunkOperation operation)
    {
        var row = (GitDiffLineViewModel)parameter!;
        var action = operation == GitHunkOperation.Stage ? "Staged" : "Unstaged";
        return RunMutationAsync(
            () => gitService.ApplyHunkAsync(repositoryRoot!, row.HunkPatch!, operation),
            $"{action} hunk in {SelectedFile!.Path}");
    }

    private async Task DiscardHunkAsync(object? parameter)
    {
        var row = (GitDiffLineViewModel)parameter!;
        if (!userInteractionService.ConfirmDestructiveAction(
                "Discard Git hunk",
                $"This will discard only this working-tree hunk from {SelectedFile!.DisplayPath}:\n\n{row.Content}\n\nThis cannot be undone. Continue?"))
        {
            StatusMessage = "Discard hunk cancelled";
            return;
        }

        await RunMutationAsync(
            () => gitService.ApplyHunkAsync(repositoryRoot!, row.HunkPatch!, GitHunkOperation.Discard),
            $"Discarded hunk from {SelectedFile.Path}").ConfigureAwait(true);
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
        comparisonDiffs.Clear();
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
        _ = LoadSelectedScopeAsync(preferredFilePath);
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
    private bool CanShowWorkingDiff() => !isShuttingDown() && !IsBusy && IsRepository &&
        SelectedRepository?.State.ChangedFiles.Any(file => file.HasWorkingTreeChanges) == true;
    private bool CanShowStagedDiff() => !isShuttingDown() && !IsBusy && IsRepository &&
        SelectedRepository?.State.ChangedFiles.Any(file => file.IsStaged) == true;
    private bool CanStage() => CanMutateSelectedFile() && SelectedFile?.HasWorkingTreeChanges == true;
    private bool CanUnstage() => CanMutateSelectedFile() && SelectedFile?.IsStaged == true;
    private bool CanStageHunk(object? parameter) =>
        CanMutateSelectedFile() && parameter is GitDiffLineViewModel { CanStageHunk: true } row && SelectedDiffLines.Contains(row);
    private bool CanUnstageHunk(object? parameter) =>
        CanMutateSelectedFile() && parameter is GitDiffLineViewModel { CanUnstageHunk: true } row && SelectedDiffLines.Contains(row);
    private bool CanDiscardHunk(object? parameter) =>
        CanMutateSelectedFile() && parameter is GitDiffLineViewModel { CanDiscardHunk: true } row && SelectedDiffLines.Contains(row);
    private bool CanMutateSelectedFile() => !isShuttingDown() && !IsBusy && IsRepository && SelectedFile is not null &&
        SelectedDiffScope.Scope is GitDiffScope.Unstaged or GitDiffScope.Staged;
    private bool CanCommit() => !isShuttingDown() && !IsBusy && IsRepository && !string.IsNullOrWhiteSpace(CommitMessage) &&
        SelectedRepository?.State.ChangedFiles.Any(file => file.IsStaged) == true;
    private bool CanOpenProjectTarget() => !isShuttingDown() && !string.IsNullOrWhiteSpace(contextProvider().ProjectPath);
    private bool CanBeginAddComment(object? parameter) =>
        !isShuttingDown() && !IsBusy && IsRepository && SelectedFile is not null && SelectedDiffScope.Scope != GitDiffScope.LastTurn &&
        parameter is GitDiffLineViewModel { CanAddComment: true, IsCommentEditorOpen: false };
    private static bool CanCancelAddComment(object? parameter) =>
        parameter is GitDiffLineViewModel { IsCommentEditorOpen: true };
    private static bool CanSaveComment(object? parameter) =>
        parameter is GitDiffLineViewModel { IsCommentEditorOpen: true };
    private bool CanBeginEditComment(object? parameter) =>
        !isShuttingDown() && parameter is GitInlineCommentViewModel { IsEditing: false } comment && ReviewComments.Contains(comment);
    private bool CanCancelEditComment(object? parameter) =>
        parameter is GitInlineCommentViewModel { IsEditing: true } comment && ReviewComments.Contains(comment);
    private bool CanSaveEditedComment(object? parameter) => CanCancelEditComment(parameter);
    private bool CanRemoveComment(object? parameter) =>
        !isShuttingDown() && parameter is GitInlineCommentViewModel comment && ReviewComments.Contains(comment);
}
