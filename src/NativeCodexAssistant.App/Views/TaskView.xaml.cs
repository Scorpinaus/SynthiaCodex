using System.Windows.Controls;

namespace NativeCodexAssistant.App.Views;

public partial class TaskView : UserControl
{
    public TaskView() => InitializeComponent();

    public void FocusComposer(bool isTurnRunning)
    {
        var composer = isTurnRunning ? GuidanceBox : PromptBox;
        composer.Focus();
        composer.CaretIndex = composer.Text.Length;
    }
}
