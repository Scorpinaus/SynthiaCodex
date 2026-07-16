using System.Windows;
using System.Windows.Controls;

namespace NativeCodexAssistant.App.Views;

public partial class ProjectThreadView : UserControl
{
    public ProjectThreadView() => InitializeComponent();

    private void OnThreadActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
