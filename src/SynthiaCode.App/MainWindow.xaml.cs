using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;

namespace SynthiaCode.App;

public partial class MainWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTMAXBUTTON = 9;

    private readonly MainViewModel viewModel;
    private HwndSource? windowSource;
    private bool shutdownStarted;
    private bool shutdownComplete;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        windowSource?.AddHook(WindowMessageHook);
        viewModel.UpdateViewportWidth(ActualWidth);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        windowSource?.RemoveHook(WindowMessageHook);
        windowSource = null;
        viewModel.CloseRequested -= OnCloseRequested;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WM_NCHITTEST || !MaximizeButton.IsVisible)
        {
            return IntPtr.Zero;
        }

        var screenPoint = new Point(
            unchecked((short)((long)lParam & 0xFFFF)),
            unchecked((short)(((long)lParam >> 16) & 0xFFFF)));
        var buttonPoint = MaximizeButton.PointFromScreen(screenPoint);
        if (buttonPoint.X >= 0 &&
            buttonPoint.Y >= 0 &&
            buttonPoint.X <= MaximizeButton.ActualWidth &&
            buttonPoint.Y <= MaximizeButton.ActualHeight)
        {
            handled = true;
            return new IntPtr(HTMAXBUTTON);
        }

        return IntPtr.Zero;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        viewModel.UpdateViewportWidth(e.NewSize.Width);
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnTitleBarMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var position = PointToScreen(e.GetPosition(this));
        SystemCommands.ShowSystemMenu(this, position);
        e.Handled = true;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            !viewModel.ApprovalQueue.HasPendingApproval &&
            viewModel.IsShellOverlayVisible)
        {
            viewModel.DismissShellOverlayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.K)
        {
            if (!viewModel.IsProjectRailOpen)
            {
                viewModel.ToggleProjectRailCommand.Execute(null);
            }

            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => FindVisibleDescendant<ProjectThreadView>()?.FocusSearch());
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            TaskFeature.FocusFindInChat();
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsTurnRunning))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => TaskFeature.FocusComposer(viewModel.IsTurnRunning));
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (shutdownStarted)
        {
            return;
        }

        shutdownStarted = true;
        IsEnabled = false;

        try
        {
            await viewModel.ShutdownAsync().ConfigureAwait(true);
        }
        finally
        {
            shutdownComplete = true;
            _ = ScheduleClose(
                Dispatcher,
                () =>
                {
                    IsEnabled = true;
                    Close();
                });
        }
    }

    private T? FindVisibleDescendant<T>()
        where T : FrameworkElement =>
        FindVisualDescendants<T>(this).FirstOrDefault(element => element.IsVisible);

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DispatcherOperation ScheduleClose(Dispatcher dispatcher, Action close) =>
        dispatcher.BeginInvoke(DispatcherPriority.Normal, close);
}
