using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using SynthiaCode.App.ViewModels;

namespace SynthiaCode.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
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

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        viewModel.UpdateViewportWidth(e.NewSize.Width);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
                NavigationFeature.FocusSearch);
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
            () =>
            {
                TaskFeature.FocusComposer(viewModel.IsTurnRunning);
            });
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete)
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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

    private static DispatcherOperation ScheduleClose(Dispatcher dispatcher, Action close) =>
        dispatcher.BeginInvoke(DispatcherPriority.Normal, close);
}
