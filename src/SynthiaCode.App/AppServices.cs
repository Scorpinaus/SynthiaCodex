using SynthiaCode.App.Services;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure;
using SynthiaCode.Infrastructure.Auth;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Logging;
using SynthiaCode.Infrastructure.Git;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Settings;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Infrastructure.Worktrees;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Infrastructure.Terminal;
using System.IO;
using System.Reflection;

namespace SynthiaCode.App;

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
        IAppLogger logger,
        IAttachmentStore attachmentStore,
        WorkspaceAttachmentResolver workspaceAttachmentResolver)
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
        AttachmentStore = attachmentStore;
        WorkspaceAttachmentResolver = workspaceAttachmentResolver;
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

    public IAttachmentStore AttachmentStore { get; }

    public WorkspaceAttachmentResolver WorkspaceAttachmentResolver { get; }

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
                "synthiacode",
                "SynthiaCode",
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
        var attachmentStore = new LocalAttachmentStore(Path.Combine(appDataDirectory, "attachments"), logger);
        var workspaceAttachmentResolver = new WorkspaceAttachmentResolver();

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
            logger,
            attachmentStore,
            workspaceAttachmentResolver);
    }
}
