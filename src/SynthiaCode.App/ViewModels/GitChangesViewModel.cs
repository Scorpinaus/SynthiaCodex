using System.Collections.ObjectModel;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;

namespace SynthiaCode.App.ViewModels;

public sealed class GitChangesViewModel : ObservableObject
{
    private readonly GitViewModel owner;

    internal GitChangesViewModel(GitViewModel owner)
    {
        this.owner = owner;
        owner.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
    }

    public ObservableCollection<GitChangedFile> ChangedFiles => owner.ChangedFiles;
    public IReadOnlyList<GitDiffScopeOption> DiffScopes => owner.DiffScopes;
    public ObservableCollection<string> ReviewBranches => owner.ReviewBranches;
    public ObservableCollection<GitReviewCommit> ReviewCommits => owner.ReviewCommits;
    public ObservableCollection<GitDiffLineViewModel> SelectedDiffLines => owner.SelectedDiffLines;
    public ObservableCollection<CodexReviewFinding> UnmatchedReviewFindings => owner.UnmatchedReviewFindings;
    public ObservableCollection<GitInlineCommentViewModel> ReviewComments => owner.ReviewComments;
    public GitDiffScopeOption SelectedDiffScope
    {
        get => owner.SelectedDiffScope;
        set => owner.SelectedDiffScope = value;
    }
    public string? SelectedReviewBranch
    {
        get => owner.SelectedReviewBranch;
        set => owner.SelectedReviewBranch = value;
    }
    public GitReviewCommit? SelectedReviewCommit
    {
        get => owner.SelectedReviewCommit;
        set => owner.SelectedReviewCommit = value;
    }
    public GitChangedFile? SelectedFile
    {
        get => owner.SelectedFile;
        set => owner.SelectedFile = value;
    }
    public string SelectedDiff => owner.SelectedDiff;
    public string DiffViewLabel => owner.DiffViewLabel;
    public bool ShowsBranchSelector => owner.ShowsBranchSelector;
    public bool ShowsCommitSelector => owner.ShowsCommitSelector;
    public bool IsHistoricalScope => owner.IsHistoricalScope;
    public bool HasUnmatchedReviewFindings => owner.HasUnmatchedReviewFindings;
    public bool HasReviewComments => owner.HasReviewComments;
    public string PendingReviewCommentsLabel => owner.PendingReviewCommentsLabel;
}
