using System.Windows;
using NativeCodexAssistant.App.ViewModels;

namespace NativeCodexAssistant.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
