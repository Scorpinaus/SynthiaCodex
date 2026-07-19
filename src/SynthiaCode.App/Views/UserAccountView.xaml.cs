using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SynthiaCode.App.ViewModels;

namespace SynthiaCode.App.Views;

public partial class UserAccountView : UserControl
{
    public UserAccountView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel account)
        {
            account.Activate();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is AccountViewModel { IsFlyoutOpen: true } account)
        {
            account.IsFlyoutOpen = false;
            AccountToggle.Focus();
            e.Handled = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel account)
        {
            account.IsFlyoutOpen = false;
        }
    }
}
