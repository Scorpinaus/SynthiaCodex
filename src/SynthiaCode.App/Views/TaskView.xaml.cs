using System.Windows.Controls;

namespace SynthiaCode.App.Views;

public partial class TaskView : UserControl
{
    public TaskView()
    {
        InitializeComponent();
    }

    public void FocusComposer(bool isTurnRunning) =>
        ComposerFeature.FocusComposer(isTurnRunning);

    public void FocusFindInChat() =>
        ConversationFeature.FocusFindInChat();
}
