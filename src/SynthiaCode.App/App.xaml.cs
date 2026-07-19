using System.Threading;
using System.Diagnostics;
using System.Windows;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.App;

public partial class App : Application
{
    private const string MutexName = "SynthiaCode.SingleInstance";

    private Mutex? instanceMutex;
    private MainViewModel? mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupTimer = Stopwatch.StartNew();
        instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "SynthiaCode is already running.",
                "SynthiaCode",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var services = AppServices.Create();
        RegisterExceptionHandlers(services.Logger);

        mainViewModel = new MainViewModel(
            services.SettingsStore,
            services.CodexDiscoveryService,
            services.AppServerSessionCoordinator,
            services.AuthService,
            services.GitService,
            services.WorktreeService,
            services.RecentProjectService,
            services.FolderPicker,
            services.UserInteractionService,
            services.ThemeService,
            services.CodexCliUtilityRunner,
            services.ThreadStore,
            services.ThreadWorkspace,
            services.TerminalService,
            services.Logger);

        MainWindow = new MainWindow(mainViewModel);
        MainWindow.Show();
        services.Logger.Log(
            AppLogLevel.Information,
            "startup_shell_visible",
            "The application shell became visible.",
            new Dictionary<string, string?> { ["elapsedMilliseconds"] = startupTimer.ElapsedMilliseconds.ToString() });
        _ = InitializeMainViewModelAsync(services.Logger, startupTimer);

        base.OnStartup(e);
    }

    private async Task InitializeMainViewModelAsync(IAppLogger logger, Stopwatch startupTimer)
    {
        if (mainViewModel is null)
        {
            return;
        }

        await mainViewModel.InitializeAsync().ConfigureAwait(true);
        logger.Log(
            AppLogLevel.Information,
            "startup_ready",
            "The application completed startup diagnostics and became ready.",
            new Dictionary<string, string?> { ["elapsedMilliseconds"] = startupTimer.ElapsedMilliseconds.ToString() });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (mainViewModel is not null)
        {
            mainViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void RegisterExceptionHandlers(IAppLogger logger)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Log(AppLogLevel.Error, "dispatcher_unhandled_exception", "An unhandled UI exception occurred.", exception: args.Exception);
            MessageBox.Show(args.Exception.Message, "SynthiaCode", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                logger.Log(AppLogLevel.Critical, "appdomain_unhandled_exception", "An unhandled application exception occurred.", exception: exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Log(AppLogLevel.Error, "unobserved_task_exception", "An unobserved task exception occurred.", exception: args.Exception);
            args.SetObserved();
        };
    }
}
