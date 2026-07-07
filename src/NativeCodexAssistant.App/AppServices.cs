using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure;
using NativeCodexAssistant.Infrastructure.Auth;
using NativeCodexAssistant.Infrastructure.Codex;
using NativeCodexAssistant.Infrastructure.Logging;
using NativeCodexAssistant.Infrastructure.Projects;
using NativeCodexAssistant.Infrastructure.Settings;

namespace NativeCodexAssistant.App;

public sealed class AppServices
{
    private AppServices(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        IAuthService authService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IAppLogger logger)
    {
        SettingsStore = settingsStore;
        CodexDiscoveryService = codexDiscoveryService;
        AuthService = authService;
        RecentProjectService = recentProjectService;
        FolderPicker = folderPicker;
        Logger = logger;
    }

    public ISettingsStore SettingsStore { get; }

    public ICodexDiscoveryService CodexDiscoveryService { get; }

    public IAuthService AuthService { get; }

    public IRecentProjectService RecentProjectService { get; }

    public IFolderPicker FolderPicker { get; }

    public IAppLogger Logger { get; }

    public static AppServices Create()
    {
        var appDataDirectory = SystemPaths.AppDataDirectory;
        var logger = new FileAppLogger(appDataDirectory);
        var settingsStore = new JsonSettingsStore(appDataDirectory, logger);
        var codexDiscoveryService = new CodexDiscoveryService(logger);
        var authService = new CodexAuthService(logger);
        var recentProjectService = new RecentProjectService();
        var folderPicker = new WpfFolderPicker();

        logger.Log(AppLogLevel.Information, "app_services_created", "Application services were created.");

        return new AppServices(
            settingsStore,
            codexDiscoveryService,
            authService,
            recentProjectService,
            folderPicker,
            logger);
    }
}
