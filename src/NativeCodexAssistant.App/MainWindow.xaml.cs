using System.Windows;
using System.ComponentModel;
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
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete)
        {
            viewModel.CloseRequested -= OnCloseRequested;
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
