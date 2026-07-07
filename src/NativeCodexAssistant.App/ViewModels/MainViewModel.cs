using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure.Codex;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly ICodexDiscoveryService codexDiscoveryService;
    private readonly ICodexProcessService codexProcessService;
    private readonly IAuthService authService;
    private readonly IRecentProjectService recentProjectService;
    private readonly IFolderPicker folderPicker;
    private readonly IAppLogger logger;
    private readonly CodexThreadService threadService = new();
    private readonly SynchronizationContext? synchronizationContext;
    private readonly AsyncRelayCommand submitPromptCommand;
    private readonly AsyncRelayCommand cancelTurnCommand;

    private AppSettings settings = new();
    private CodexInstallation currentCodex = CodexInstallation.Missing("Detection has not run yet.");
    private AuthenticationState currentAuth = new(AuthReadiness.Unknown, "Checking sign-in", "Authentication detection has not run yet.", null);
    private string? selectedProjectPath;
    private string? activeThreadId;
    private string? activeTurnId;
    private string promptText = string.Empty;
    private string statusMessage = "Starting";
    private bool isBusy;
    private bool isTurnRunning;
    private CodexAppServerClient? appServerClient;

    public MainViewModel(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        ICodexProcessService codexProcessService,
        IAuthService authService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IAppLogger logger)
    {
        this.settingsStore = settingsStore;
        this.codexDiscoveryService = codexDiscoveryService;
        this.codexProcessService = codexProcessService;
        this.authService = authService;
        this.recentProjectService = recentProjectService;
        this.folderPicker = folderPicker;
        this.logger = logger;
        synchronizationContext = SynchronizationContext.Current;

        BrowseProjectCommand = new AsyncRelayCommand(BrowseProjectAsync);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync);
        OpenRecentProjectCommand = new AsyncRelayCommand(OpenRecentProjectAsync);
        SignInChatGptCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.ChatGpt));
        SignInDeviceCodeCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.DeviceCode));
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        SubmitPromptCommand = submitPromptCommand = new AsyncRelayCommand(SubmitPromptAsync);
        CancelTurnCommand = cancelTurnCommand = new AsyncRelayCommand(CancelTurnAsync, CanCancelTurn);
    }

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ObservableCollection<CodexTimelineItem> TimelineItems => threadService.TimelineItems;

    public ObservableCollection<string> RawEvents => threadService.RawEvents;

    public ICommand BrowseProjectCommand { get; }

    public ICommand RefreshDiagnosticsCommand { get; }

    public ICommand OpenRecentProjectCommand { get; }

    public ICommand SignInChatGptCommand { get; }

    public ICommand SignInDeviceCodeCommand { get; }

    public ICommand SignOutCommand { get; }

    public ICommand SubmitPromptCommand { get; }

    public ICommand CancelTurnCommand { get; }

    public string? SelectedProjectPath
    {
        get => selectedProjectPath;
        private set
        {
            if (SetProperty(ref selectedProjectPath, value))
            {
                OnPropertyChanged(nameof(SelectedProjectName));
            }
        }
    }

    public string SelectedProjectName =>
        string.IsNullOrWhiteSpace(SelectedProjectPath)
            ? "No project selected"
            : new DirectoryInfo(SelectedProjectPath).Name;

    public string CodexSummary => currentCodex.Summary;

    public string CodexExecutablePath => currentCodex.ExecutablePath ?? "Not found";

    public string CodexVersion => currentCodex.Version ?? "Unknown";

    public string AuthSummary => currentAuth.Summary;

    public string AuthDetail => currentAuth.Detail;

    public string CodexHome => currentAuth.CodexHome ?? "Default not resolved";

    public string SettingsPath => settingsStore.SettingsPath;

    public string PromptText
    {
        get => promptText;
        set => SetProperty(ref promptText, value);
    }

    public string FinalResponse =>
        string.IsNullOrWhiteSpace(threadService.FinalResponse)
            ? "No final response yet"
            : threadService.FinalResponse;

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public bool IsTurnRunning
    {
        get => isTurnRunning;
        private set
        {
            if (SetProperty(ref isTurnRunning, value))
            {
                submitPromptCommand.RaiseCanExecuteChanged();
                cancelTurnCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        logger.Log(AppLogLevel.Information, "view_model_initialize", "Main view model initialization started.");
        settings = await settingsStore.LoadAsync().ConfigureAwait(true);
        RefreshRecentProjects();
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
        StatusMessage = "Ready";
    }

    private async Task BrowseProjectAsync()
    {
        var selectedPath = folderPicker.PickFolder(SelectedProjectPath);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        await SelectProjectAsync(selectedPath).ConfigureAwait(true);
    }

    private async Task OpenRecentProjectAsync(object? parameter)
    {
        if (parameter is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            StatusMessage = "Recent project path no longer exists";
            return;
        }

        await SelectProjectAsync(path).ConfigureAwait(true);
    }

    private async Task SelectProjectAsync(string path)
    {
        SelectedProjectPath = Path.GetFullPath(path);
        activeThreadId = null;
        activeTurnId = null;
        threadService.Reset();
        OnPropertyChanged(nameof(FinalResponse));
        recentProjectService.AddRecentProject(settings, SelectedProjectPath);
        RefreshRecentProjects();
        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        StatusMessage = $"Project selected: {SelectedProjectName}";
        logger.Log(
            AppLogLevel.Information,
            "project_selected",
            "A project was selected.",
            new Dictionary<string, string?> { ["path"] = SelectedProjectPath });
    }

    private async Task RefreshDiagnosticsAsync()
    {
        IsBusy = true;
        StatusMessage = "Refreshing diagnostics";

        try
        {
            currentCodex = await codexDiscoveryService.DetectAsync(settings.PreferredCodexPath).ConfigureAwait(true);
            currentAuth = await authService.GetAuthenticationStateAsync(currentCodex).ConfigureAwait(true);
            RefreshDiagnosticLines();
            RaiseComputedProperties();
            StatusMessage = "Diagnostics refreshed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartLoginAsync(LoginMethod method)
    {
        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before signing in";
            return;
        }

        var started = await authService.StartLoginAsync(currentCodex, method).ConfigureAwait(true);
        StatusMessage = started ? "Sign-in opened in a terminal window" : "Could not start sign-in";
    }

    private async Task SignOutAsync()
    {
        if (!currentCodex.IsFound)
        {
            StatusMessage = "Codex CLI is not available";
            return;
        }

        var started = await authService.StartLogoutAsync(currentCodex).ConfigureAwait(true);
        StatusMessage = started ? "Sign-out opened in a terminal window" : "Could not start sign-out";
    }

    private async Task SubmitPromptAsync()
    {
        if (IsTurnRunning)
        {
            StatusMessage = "A Codex turn is already running";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            StatusMessage = "Select a project before starting a Codex task";
            return;
        }

        if (string.IsNullOrWhiteSpace(PromptText))
        {
            StatusMessage = "Enter a prompt before starting a Codex task";
            return;
        }

        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before starting a task";
            return;
        }

        if (currentAuth.Readiness is AuthReadiness.Unavailable or AuthReadiness.NotSignedIn)
        {
            StatusMessage = "Sign in with Codex before starting a task";
            return;
        }

        IsTurnRunning = true;
        StatusMessage = "Starting Codex task";

        try
        {
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            if (activeThreadId is null)
            {
                var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
                activeThreadId = thread.ThreadId;
            }

            var submittedPrompt = PromptText.Trim();
            var turn = await client.StartTurnAsync(new CodexTurnStartRequest(
                activeThreadId,
                submittedPrompt,
                SelectedProjectPath,
                CodexSandbox.WorkspaceWrite)).ConfigureAwait(true);

            activeTurnId = turn.TurnId;
            cancelTurnCommand.RaiseCanExecuteChanged();
            StatusMessage = "Codex turn running";
        }
        catch (Exception ex)
        {
            IsTurnRunning = false;
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_task_start_failed", "Could not start Codex task.", exception: ex);
        }
    }

    private async Task CancelTurnAsync()
    {
        if (!CanCancelTurn() || appServerClient is null || string.IsNullOrWhiteSpace(activeThreadId) || string.IsNullOrWhiteSpace(activeTurnId))
        {
            StatusMessage = "No active turn to cancel";
            return;
        }

        try
        {
            await appServerClient.CancelTurnAsync(activeThreadId, activeTurnId).ConfigureAwait(true);
            StatusMessage = "Cancellation requested";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_turn_cancel_failed", "Could not cancel Codex turn.", exception: ex);
        }
    }

    private bool CanCancelTurn()
    {
        return IsTurnRunning &&
            appServerClient is not null &&
            !string.IsNullOrWhiteSpace(activeThreadId) &&
            !string.IsNullOrWhiteSpace(activeTurnId);
    }

    private async Task<CodexAppServerClient> EnsureAppServerClientAsync()
    {
        if (appServerClient is not null)
        {
            return appServerClient;
        }

        var transport = await codexProcessService.StartAppServerTransportAsync(currentCodex).ConfigureAwait(true);
        var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("native_codex_assistant", "Native Codex Assistant", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0"));
        try
        {
            client.NotificationReceived += OnAppServerNotificationReceived;
            await client.InitializeAsync().ConfigureAwait(true);
            appServerClient = client;
            return appServerClient;
        }
        catch
        {
            client.NotificationReceived -= OnAppServerNotificationReceived;
            await client.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    private void OnAppServerNotificationReceived(object? sender, AppServerNotification notification)
    {
        if (synchronizationContext is null)
        {
            ApplyNotification(notification);
            return;
        }

        synchronizationContext.Post(_ => ApplyNotification(notification), null);
    }

    private void ApplyNotification(AppServerNotification notification)
    {
        threadService.ApplyNotification(notification);
        activeTurnId = threadService.ActiveTurnId ?? activeTurnId;
        if (notification.Method == "turn/completed")
        {
            IsTurnRunning = false;
            if (threadService.ActiveTurnStatus == CodexTurnStatus.Completed)
            {
                PromptText = string.Empty;
                StatusMessage = "Codex turn completed";
            }
            else if (threadService.RequiresAuthentication)
            {
                StatusMessage = "Codex authentication failed. Sign in and retry.";
            }
            else
            {
                StatusMessage = $"Codex turn {threadService.ActiveTurnStatus.ToString().ToLowerInvariant()}";
            }
        }

        OnPropertyChanged(nameof(FinalResponse));
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var project in settings.RecentProjects)
        {
            RecentProjects.Add(project);
        }
    }

    private void RefreshDiagnosticLines()
    {
        Diagnostics.Clear();
        Diagnostics.Add($"App: {Assembly.GetExecutingAssembly().GetName().Version}");
        Diagnostics.Add($"Windows: {Environment.OSVersion.VersionString}");
        Diagnostics.Add($".NET: {Environment.Version}");
        Diagnostics.Add($"Codex: {CodexSummary}");
        Diagnostics.Add($"Codex path: {CodexExecutablePath}");
        Diagnostics.Add($"Codex version: {CodexVersion}");
        Diagnostics.Add($"Sign-in: {AuthSummary}");
        Diagnostics.Add($"Codex home: {CodexHome}");
        Diagnostics.Add($"Settings: {SettingsPath}");
    }

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(CodexSummary));
        OnPropertyChanged(nameof(CodexExecutablePath));
        OnPropertyChanged(nameof(CodexVersion));
        OnPropertyChanged(nameof(AuthSummary));
        OnPropertyChanged(nameof(AuthDetail));
        OnPropertyChanged(nameof(CodexHome));
        OnPropertyChanged(nameof(SettingsPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (appServerClient is not null)
        {
            appServerClient.NotificationReceived -= OnAppServerNotificationReceived;
            await appServerClient.DisposeAsync().ConfigureAwait(false);
            appServerClient = null;
        }
    }
}
