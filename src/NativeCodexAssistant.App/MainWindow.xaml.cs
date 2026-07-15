using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using NativeCodexAssistant.App.ViewModels;

namespace NativeCodexAssistant.App;

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
                var composer = viewModel.IsTurnRunning ? GuidanceBox : PromptBox;
                composer.Focus();
                composer.CaretIndex = composer.Text.Length;
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
            IsEnabled = true;
            Close();
        }
    }
}
