using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Git;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure;
using NativeCodexAssistant.Infrastructure.Auth;
using NativeCodexAssistant.Infrastructure.Codex;
using NativeCodexAssistant.Infrastructure.Logging;
using NativeCodexAssistant.Infrastructure.Git;
using NativeCodexAssistant.Infrastructure.Projects;
using NativeCodexAssistant.Infrastructure.Settings;

namespace NativeCodexAssistant.App;

public sealed class AppServices
{
    private AppServices(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        ICodexProcessService codexProcessService,
        IAuthService authService,
        IGitService gitService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IUserInteractionService userInteractionService,
        IAppLogger logger)
    {
        SettingsStore = settingsStore;
        CodexDiscoveryService = codexDiscoveryService;
        CodexProcessService = codexProcessService;
        AuthService = authService;
        GitService = gitService;
        RecentProjectService = recentProjectService;
        FolderPicker = folderPicker;
        UserInteractionService = userInteractionService;
        Logger = logger;
    }

    public ISettingsStore SettingsStore { get; }

    public ICodexDiscoveryService CodexDiscoveryService { get; }

    public ICodexProcessService CodexProcessService { get; }

    public IAuthService AuthService { get; }

    public IGitService GitService { get; }

    public IRecentProjectService RecentProjectService { get; }

    public IFolderPicker FolderPicker { get; }

    public IUserInteractionService UserInteractionService { get; }

    public IAppLogger Logger { get; }

    public static AppServices Create()
    {
        var appDataDirectory = SystemPaths.AppDataDirectory;
        var logger = new FileAppLogger(appDataDirectory);
        var settingsStore = new JsonSettingsStore(appDataDirectory, logger);
        var codexDiscoveryService = new CodexDiscoveryService(logger);
        var codexProcessService = new CodexProcessService(logger);
        var authService = new CodexAuthService(logger);
        var gitService = new GitService(logger);
        var recentProjectService = new RecentProjectService();
        var folderPicker = new WpfFolderPicker();
        var userInteractionService = new WpfUserInteractionService();

        logger.Log(AppLogLevel.Information, "app_services_created", "Application services were created.");

        return new AppServices(
            settingsStore,
            codexDiscoveryService,
            codexProcessService,
            authService,
            gitService,
            recentProjectService,
            folderPicker,
            userInteractionService,
            logger);
    }
}
