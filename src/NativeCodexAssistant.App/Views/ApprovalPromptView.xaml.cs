using System.Windows.Controls;
using System.Windows.Input;
using NativeCodexAssistant.App.ViewModels;

namespace NativeCodexAssistant.App.Views;

public partial class ApprovalPromptView : UserControl
{
    public ApprovalPromptView() => InitializeComponent();

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e) =>
        CancelButton.Focus();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.ApprovalQueue.CancelCommand.CanExecute(null))
        {
            viewModel.ApprovalQueue.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
