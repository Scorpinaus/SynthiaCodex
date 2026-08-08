using SynthiaCode.App.Services;
using SynthiaCode.Application.Conversations;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure;
using SynthiaCode.Infrastructure.Auth;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Codex.Configuration;
using SynthiaCode.Infrastructure.Logging;
using SynthiaCode.Infrastructure.Git;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Settings;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Infrastructure.Worktrees;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Infrastructure.Terminal;
using SynthiaCode.Core.Workspaces;
using SynthiaCode.Infrastructure.Workspaces;
using SynthiaCode.Harnesses.Codex;
using System.IO;
using System.Reflection;

namespace SynthiaCode.App;

public sealed class AppServices
{
    private AppServices(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        IAppServerSessionCoordinator appServerSessionCoordinator,
        IHarnessRegistry harnessRegistry,
        IHarnessRuntimeCoordinator harnessRuntimeCoordinator,
        IAuthService authService,
        IGitService gitService,
        IWorktreeService worktreeService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IUserInteractionService userInteractionService,
        IThemeService themeService,
        ICodexCliUtilityRunner codexCliUtilityRunner,
        ITerminalService terminalService,
        IAppLogger logger,
        IGeneralWorkspaceService generalWorkspaceService,
        IAttachmentStore attachmentStore,
        WorkspaceAttachmentResolver workspaceAttachmentResolver,
        ISharedCodexConfigurationService sharedCodexConfigurationService,
        IConversationFeatureFacade conversationFeature,
        CodeReviewUseCaseService codeReviewService,
        ProjectWorkspaceOperations projectWorkspaceOperations,
        IProjectTrustService projectTrustService,
        AttachmentDraftOrchestrationService attachmentDraftService,
        ISpeechRecognitionService speechRecognitionService)
    {
        SettingsStore = settingsStore;
        CodexDiscoveryService = codexDiscoveryService;
        AppServerSessionCoordinator = appServerSessionCoordinator;
        HarnessRegistry = harnessRegistry;
        HarnessRuntimeCoordinator = harnessRuntimeCoordinator;
        AuthService = authService;
        GitService = gitService;
        WorktreeService = worktreeService;
        RecentProjectService = recentProjectService;
        FolderPicker = folderPicker;
        UserInteractionService = userInteractionService;
        ThemeService = themeService;
        CodexCliUtilityRunner = codexCliUtilityRunner;
        TerminalService = terminalService;
        Logger = logger;
        GeneralWorkspaceService = generalWorkspaceService;
        AttachmentStore = attachmentStore;
        WorkspaceAttachmentResolver = workspaceAttachmentResolver;
        SharedCodexConfigurationService = sharedCodexConfigurationService;
        ConversationFeature = conversationFeature;
        CodeReviewService = codeReviewService;
        ProjectWorkspaceOperations = projectWorkspaceOperations;
        ProjectTrustService = projectTrustService;
        AttachmentDraftService = attachmentDraftService;
        SpeechRecognitionService = speechRecognitionService;
    }

    public ISettingsStore SettingsStore { get; }

    public ICodexDiscoveryService CodexDiscoveryService { get; }

    public IAppServerSessionCoordinator AppServerSessionCoordinator { get; }

    public IHarnessRegistry HarnessRegistry { get; }

    public IHarnessRuntimeCoordinator HarnessRuntimeCoordinator { get; }

    public IAuthService AuthService { get; }

    public IGitService GitService { get; }

    public IWorktreeService WorktreeService { get; }

    public IRecentProjectService RecentProjectService { get; }

    public IFolderPicker FolderPicker { get; }

    public IUserInteractionService UserInteractionService { get; }

    public IThemeService ThemeService { get; }

    public ICodexCliUtilityRunner CodexCliUtilityRunner { get; }

    public ITerminalService TerminalService { get; }

    public IAppLogger Logger { get; }

    public IGeneralWorkspaceService GeneralWorkspaceService { get; }

    public IAttachmentStore AttachmentStore { get; }

    public WorkspaceAttachmentResolver WorkspaceAttachmentResolver { get; }

    public ISharedCodexConfigurationService SharedCodexConfigurationService { get; }

    public IConversationFeatureFacade ConversationFeature { get; }

    public CodeReviewUseCaseService CodeReviewService { get; }

    public ProjectWorkspaceOperations ProjectWorkspaceOperations { get; }

    public IProjectTrustService ProjectTrustService { get; }

    public AttachmentDraftOrchestrationService AttachmentDraftService { get; }

    public ISpeechRecognitionService SpeechRecognitionService { get; }

    public static AppServices Create()
    {
        var appDataDirectory = SystemPaths.AppDataDirectory;
        var logger = new FileAppLogger(appDataDirectory);
        var settingsStore = new CoalescingSettingsStore(
            new JsonSettingsStore(appDataDirectory, logger),
            logger);
        var codexRuntimeEnvironment = new CodexRuntimeEnvironment(SystemPaths.CodexHomeDirectory);
        CodexDiagnosticStoreMaintenance.TrimOversizedStore(codexRuntimeEnvironment.HomePath, logger);
        var codexDiscoveryService = new CodexDiscoveryService(logger, codexRuntimeEnvironment);
        var codexProcessService = new CodexProcessService(logger, codexRuntimeEnvironment);
        var appServerSessionCoordinator = new AppServerSessionCoordinator(
            codexProcessService,
            logger,
            new CodexAppServerClientMetadata(
                "synthiacode",
                "SynthiaCode",
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0"));
        var harnessRegistry = new HarnessRegistry([
            new CodexHarness(codexDiscoveryService, appServerSessionCoordinator)
        ]);
        var harnessRuntimeCoordinator = new HarnessRuntimeCoordinator(harnessRegistry);
        var harnessOperations = new HarnessOperations(harnessRuntimeCoordinator);
        var authService = new CodexAuthService(logger, codexRuntimeEnvironment);
        var gitService = new GitService(logger);
        var worktreeService = new WorktreeService(logger);
        var recentProjectService = new RecentProjectService();
        var folderPicker = new WpfFolderPicker();
        var userInteractionService = new WpfUserInteractionService();
        var themeService = new WpfThemeService();
        var codexCliUtilityRunner = new CodexCliUtilityRunner(logger, codexRuntimeEnvironment);
        var threadStore = new ThreadStore();
        var threadWorkspace = new CodexThreadWorkspace();
        var terminalService = new WindowsConPtyTerminalService(logger);
        var generalWorkspaceService = new GeneralWorkspaceService(appDataDirectory);
        var attachmentStore = new LocalAttachmentStore(Path.Combine(appDataDirectory, "attachments"), logger);
        var workspaceAttachmentResolver = new WorkspaceAttachmentResolver();
        var sharedCodexConfigurationService =
            new SharedCodexConfigurationService(codexRuntimeEnvironment.HomePath);
        var followUpQueues = new CodexFollowUpQueueWorkspace();
        var conversationFeature = new ConversationFeatureFacade(
            harnessOperations,
            gitService,
            worktreeService,
            settingsStore,
            threadStore,
            threadWorkspace,
            followUpQueues);
        var codeReviewService = new CodeReviewUseCaseService(
            appServerSessionCoordinator,
            conversationFeature.Workspace);
        var projectWorkspaceOperations = new ProjectWorkspaceOperations(
            gitService, worktreeService, recentProjectService, generalWorkspaceService);
        var projectTrustService = new ProjectTrustService(
            appServerSessionCoordinator,
            userInteractionService,
            logger);
        var attachmentDraftService = new AttachmentDraftOrchestrationService(
            attachmentStore,
            workspaceAttachmentResolver,
            new CodexTurnRequestFactory(attachmentStore, workspaceAttachmentResolver),
            logger);
        var speechRecognitionService = new SystemSpeechRecognitionService(logger);

        logger.Log(AppLogLevel.Information, "app_services_created", "Application services were created.");

        return new AppServices(
            settingsStore,
            codexDiscoveryService,
            appServerSessionCoordinator,
            harnessRegistry,
            harnessRuntimeCoordinator,
            authService,
            gitService,
            worktreeService,
            recentProjectService,
            folderPicker,
            userInteractionService,
            themeService,
            codexCliUtilityRunner,
            terminalService,
            logger,
            generalWorkspaceService,
            attachmentStore,
            workspaceAttachmentResolver,
            sharedCodexConfigurationService,
            conversationFeature,
            codeReviewService,
            projectWorkspaceOperations,
            projectTrustService,
            attachmentDraftService,
            speechRecognitionService);
    }
}
