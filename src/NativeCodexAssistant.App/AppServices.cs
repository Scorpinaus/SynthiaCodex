using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Git;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Infrastructure;
using NativeCodexAssistant.Infrastructure.Auth;
using NativeCodexAssistant.Infrastructure.Codex;
using NativeCodexAssistant.Infrastructure.Logging;
using NativeCodexAssistant.Infrastructure.Git;
using NativeCodexAssistant.Infrastructure.Projects;
using NativeCodexAssistant.Infrastructure.Settings;
using NativeCodexAssistant.Core.Worktrees;
using NativeCodexAssistant.Infrastructure.Worktrees;

namespace NativeCodexAssistant.App;

public sealed class AppServices
{
    private AppServices(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        ICodexProcessService codexProcessService,
        IAuthService authService,
        IGitService gitService,
        IWorktreeService worktreeService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IUserInteractionService userInteractionService,
        IThemeService themeService,
        ICodexCliUtilityRunner codexCliUtilityRunner,
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace,
        IAppLogger logger)
    {
        SettingsStore = settingsStore;
        CodexDiscoveryService = codexDiscoveryService;
        CodexProcessService = codexProcessService;
        AuthService = authService;
        GitService = gitService;
        WorktreeService = worktreeService;
        RecentProjectService = recentProjectService;
        FolderPicker = folderPicker;
        UserInteractionService = userInteractionService;
        ThemeService = themeService;
        CodexCliUtilityRunner = codexCliUtilityRunner;
        ThreadStore = threadStore;
        ThreadWorkspace = threadWorkspace;
        Logger = logger;
    }

    public ISettingsStore SettingsStore { get; }

    public ICodexDiscoveryService CodexDiscoveryService { get; }

    public ICodexProcessService CodexProcessService { get; }

    public IAuthService AuthService { get; }

    public IGitService GitService { get; }

    public IWorktreeService WorktreeService { get; }

    public IRecentProjectService RecentProjectService { get; }

    public IFolderPicker FolderPicker { get; }

    public IUserInteractionService UserInteractionService { get; }

    public IThemeService ThemeService { get; }

    public ICodexCliUtilityRunner CodexCliUtilityRunner { get; }

    public ThreadStore ThreadStore { get; }

    public CodexThreadWorkspace ThreadWorkspace { get; }

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
        var worktreeService = new WorktreeService(logger);
        var recentProjectService = new RecentProjectService();
        var folderPicker = new WpfFolderPicker();
        var userInteractionService = new WpfUserInteractionService();
        var themeService = new WpfThemeService();
        var codexCliUtilityRunner = new CodexCliUtilityRunner(logger);
        var threadStore = new ThreadStore();
        var threadWorkspace = new CodexThreadWorkspace();

        logger.Log(AppLogLevel.Information, "app_services_created", "Application services were created.");

        return new AppServices(
            settingsStore,
            codexDiscoveryService,
            codexProcessService,
            authService,
            gitService,
            worktreeService,
            recentProjectService,
            folderPicker,
            userInteractionService,
            themeService,
            codexCliUtilityRunner,
            threadStore,
            threadWorkspace,
            logger);
    }
}
