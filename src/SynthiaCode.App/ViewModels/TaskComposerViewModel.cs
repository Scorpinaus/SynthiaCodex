using System.Collections.ObjectModel;
using System.Windows.Input;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskComposerViewModel : ObservableObject
{
    private readonly TaskViewModel owner;

    internal TaskComposerViewModel(TaskViewModel owner)
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

    public string Prompt
    {
        get => owner.Prompt;
        set => owner.Prompt = value;
    }

    public string SteeringText
    {
        get => owner.SteeringText;
        set => owner.SteeringText = value;
    }

    public bool IsTurnRunning => owner.IsTurnRunning;
    public string ComposerActionLabel => owner.ComposerActionLabel;
    public string AlternateFollowUpActionLabel => owner.AlternateFollowUpActionLabel;
    public string ContextWindowIndicator => owner.ContextWindowIndicator;
    public string ContextWindowToolTip => owner.ContextWindowToolTip;
    public ObservableCollection<QueuedFollowUp> QueuedFollowUps => owner.QueuedFollowUps;
    public bool HasQueuedFollowUps => owner.HasQueuedFollowUps;
    public ObservableCollection<AttachmentReference> Attachments => owner.Attachments;
    public bool HasAttachments => owner.HasAttachments;
    public string AttachmentValidationMessage => owner.AttachmentValidationMessage;
    public bool IsDictating => owner.IsDictating;
    public string DictationAutomationName => owner.DictationAutomationName;
    public string DictationStatusText => owner.DictationStatusText;
    public string DictationToolTip => owner.DictationToolTip;
    public ComposerSkillSelectorViewModel SkillSelector => owner.SkillSelector;
    public ICommand SubmitCommand => owner.SubmitCommand;
    public ICommand SteerCommand => owner.SteerCommand;
    public ICommand AlternateFollowUpCommand => owner.AlternateFollowUpCommand;
    public ICommand CancelCommand => owner.CancelCommand;
    public ICommand ToggleDictationCommand => owner.ToggleDictationCommand;
    public ICommand StartCodeReviewCommand => owner.StartCodeReviewCommand;
    public ICommand RemoveAttachmentCommand => owner.RemoveAttachmentCommand;
    public ICommand MoveAttachmentLeftCommand => owner.MoveAttachmentLeftCommand;
    public ICommand MoveAttachmentRightCommand => owner.MoveAttachmentRightCommand;
    public ICommand BeginQueuedFollowUpEditCommand => owner.BeginQueuedFollowUpEditCommand;
    public ICommand CancelQueuedFollowUpEditCommand => owner.CancelQueuedFollowUpEditCommand;
    public ICommand SaveQueuedFollowUpEditCommand => owner.SaveQueuedFollowUpEditCommand;
    public ICommand MoveQueuedFollowUpUpCommand => owner.MoveQueuedFollowUpUpCommand;
    public ICommand MoveQueuedFollowUpDownCommand => owner.MoveQueuedFollowUpDownCommand;
    public ICommand DeleteQueuedFollowUpCommand => owner.DeleteQueuedFollowUpCommand;
    public ICommand SendQueuedFollowUpCommand => owner.SendQueuedFollowUpCommand;
}
