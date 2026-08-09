using System.Collections.ObjectModel;
using System.Windows.Input;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskConversationViewModel : ObservableObject
{
    private readonly TaskViewModel owner;

    internal TaskConversationViewModel(TaskViewModel owner)
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

    public ObservableCollection<CodexConversationTurn> ConversationTurns => owner.ConversationTurns;
    public bool HasConversation => owner.HasConversation;
    public bool IsFindInChatOpen => owner.IsFindInChatOpen;
    public string FindInChatText
    {
        get => owner.FindInChatText;
        set => owner.FindInChatText = value;
    }
    public string FindInChatSummary => owner.FindInChatSummary;
    public ICommand OpenFindInChatCommand => owner.OpenFindInChatCommand;
    public ICommand CloseFindInChatCommand => owner.CloseFindInChatCommand;
    public ICommand FindNextCommand => owner.FindNextCommand;
    public ICommand FindPreviousCommand => owner.FindPreviousCommand;
    public ICommand BeginPromptEditCommand => owner.BeginPromptEditCommand;
    public ICommand CancelPromptEditCommand => owner.CancelPromptEditCommand;
    public ICommand SubmitPromptEditCommand => owner.SubmitPromptEditCommand;
    public ICommand ForkConversationCommand => owner.ForkConversationCommand;
    public ICommand OpenExternalUriCommand => owner.OpenExternalUriCommand;
    public ICommand EditGeneratedImageCommand => owner.EditGeneratedImageCommand;
}
