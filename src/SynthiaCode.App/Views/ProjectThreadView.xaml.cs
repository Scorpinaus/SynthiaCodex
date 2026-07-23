using System.Windows;
using System.Windows.Controls;

namespace SynthiaCode.App.Views;

public partial class ProjectThreadView : UserControl
{
    public ProjectThreadView() => InitializeComponent();

    public void FocusSearch()
    {
        ChatSearchBox.Focus();
        ChatSearchBox.SelectAll();
    }

    private void OnThreadActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
