using System.Windows.Input;

namespace SynthiaCode.App.ViewModels;

public sealed class GitActionsViewModel : ObservableObject
{
    private readonly GitViewModel owner;

    internal GitActionsViewModel(GitViewModel owner)
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

    public string CommitMessage
    {
        get => owner.CommitMessage;
        set => owner.CommitMessage = value;
    }
    public bool IsBusy => owner.IsBusy;
    public ICommand RefreshCommand => owner.RefreshCommand;
    public ICommand ShowWorkingDiffCommand => owner.ShowWorkingDiffCommand;
    public ICommand ShowStagedDiffCommand => owner.ShowStagedDiffCommand;
    public ICommand StageCommand => owner.StageCommand;
    public ICommand UnstageCommand => owner.UnstageCommand;
    public ICommand DiscardCommand => owner.DiscardCommand;
    public ICommand StageHunkCommand => owner.StageHunkCommand;
    public ICommand UnstageHunkCommand => owner.UnstageHunkCommand;
    public ICommand DiscardHunkCommand => owner.DiscardHunkCommand;
    public ICommand CommitCommand => owner.CommitCommand;
    public ICommand PushCommand => owner.PushCommand;
    public ICommand OpenEditorCommand => owner.OpenEditorCommand;
    public ICommand RevealExplorerCommand => owner.RevealExplorerCommand;
    public ICommand BeginAddCommentCommand => owner.BeginAddCommentCommand;
    public ICommand CancelAddCommentCommand => owner.CancelAddCommentCommand;
    public ICommand SaveCommentCommand => owner.SaveCommentCommand;
    public ICommand BeginEditCommentCommand => owner.BeginEditCommentCommand;
    public ICommand CancelEditCommentCommand => owner.CancelEditCommentCommand;
    public ICommand SaveEditedCommentCommand => owner.SaveEditedCommentCommand;
    public ICommand RemoveCommentCommand => owner.RemoveCommentCommand;
}
