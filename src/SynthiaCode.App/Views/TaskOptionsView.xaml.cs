using System.Windows.Controls;
using System.Windows.Input;
using SynthiaCode.App.ViewModels;

namespace SynthiaCode.App.Views;

public partial class TaskOptionsView : UserControl
{
    public TaskOptionsView()
    {
        InitializeComponent();
    }

    private void OnModelOptionsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((DataContext as MainViewModel)?.TaskWorkspace is not { } taskViewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            taskViewModel.IsOptionsFlyoutOpen = false;
            ModelOptionsButton.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.BrowserBack &&
            taskViewModel.OptionsPage != ComposerOptionsPage.Main)
        {
            taskViewModel.OptionsPage = ComposerOptionsPage.Main;
            e.Handled = true;
        }
    }
}
