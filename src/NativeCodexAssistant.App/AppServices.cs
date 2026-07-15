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
using NativeCodexAssistant.Core.Terminal;
using NativeCodexAssistant.Infrastructure.Terminal;
using System.Reflection;

namespace NativeCodexAssistant.App;

public sealed class AppServices
{
    private AppServices(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        IAppServerSessionCoordinator appServerSessionCoordinator,
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
        ITerminalService terminalService,
        IAppLogger logger)
    {
        SettingsStore = settingsStore;
        CodexDiscoveryService = codexDiscoveryService;
        AppServerSessionCoordinator = appServerSessionCoordinator;
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
        TerminalService = terminalService;
        Logger = logger;
    }

    public ISettingsStore SettingsStore { get; }

    public ICodexDiscoveryService CodexDiscoveryService { get; }

    public IAppServerSessionCoordinator AppServerSessionCoordinator { get; }

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

    public ITerminalService TerminalService { get; }

    public IAppLogger Logger { get; }

    public static AppServices Create()
    {
        var appDataDirectory = SystemPaths.AppDataDirectory;
        var logger = new FileAppLogger(appDataDirectory);
        var settingsStore = new CoalescingSettingsStore(
            new JsonSettingsStore(appDataDirectory, logger),
            logger);
        var codexDiscoveryService = new CodexDiscoveryService(logger);
        var codexProcessService = new CodexProcessService(logger);
        var appServerSessionCoordinator = new AppServerSessionCoordinator(
            codexProcessService,
            logger,
            new CodexAppServerClientMetadata(
                "native_codex_assistant",
                "Native Codex Assistant",
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0"));
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
        var terminalService = new WindowsConPtyTerminalService(logger);

        logger.Log(AppLogLevel.Information, "app_services_created", "Application services were created.");

        return new AppServices(
            settingsStore,
            codexDiscoveryService,
            appServerSessionCoordinator,
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
            terminalService,
            logger);
    }
}
