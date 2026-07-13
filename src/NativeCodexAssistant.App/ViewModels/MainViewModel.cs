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
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly AsyncRelayCommand exitApplicationCommand;

    private AppSettings settings = new();
    private CodexInstallation currentCodex = CodexInstallation.Missing("Detection has not run yet.");
    private AuthenticationState currentAuth = new(AuthReadiness.Unknown, "Checking sign-in", "Authentication detection has not run yet.", null);
    private string? selectedProjectPath;
    private string? activeThreadId;
    private string? activeTurnId;
    private string promptText = string.Empty;
    private string modelOverride = string.Empty;
    private string reasoningEffortOverride = string.Empty;
    private string statusMessage = "Starting";
    private bool isBusy;
    private bool isTurnRunning;
    private bool activeThreadLoaded;
    private bool isShuttingDown;
    private Task? shutdownTask;
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
        LoadModelsCommand = loadModelsCommand = new AsyncRelayCommand(LoadModelOptionsAsync);
        ExitApplicationCommand = exitApplicationCommand = new AsyncRelayCommand(RequestApplicationExitAsync, () => !isShuttingDown);
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ObservableCollection<CodexTimelineItem> TimelineItems => threadService.TimelineItems;

    public ObservableCollection<string> RawEvents => threadService.RawEvents;

    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<string> ReasoningEffortOptions { get; } =
    [
        string.Empty,
        "none",
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh"
    ];

    public ICommand BrowseProjectCommand { get; }

    public ICommand RefreshDiagnosticsCommand { get; }

    public ICommand OpenRecentProjectCommand { get; }

    public ICommand SignInChatGptCommand { get; }

    public ICommand SignInDeviceCodeCommand { get; }

    public ICommand SignOutCommand { get; }

    public ICommand SubmitPromptCommand { get; }

    public ICommand CancelTurnCommand { get; }

    public ICommand LoadModelsCommand { get; }

    public ICommand ExitApplicationCommand { get; }

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

    public string ModelOverride
    {
        get => modelOverride;
        set => SetProperty(ref modelOverride, value);
    }

    public string ReasoningEffortOverride
    {
        get => reasoningEffortOverride;
        set => SetProperty(ref reasoningEffortOverride, value);
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

    public bool IsShuttingDown
    {
        get => isShuttingDown;
        private set
        {
            if (SetProperty(ref isShuttingDown, value))
            {
                exitApplicationCommand.RaiseCanExecuteChanged();
                submitPromptCommand.RaiseCanExecuteChanged();
                cancelTurnCommand.RaiseCanExecuteChanged();
                loadModelsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        logger.Log(AppLogLevel.Information, "view_model_initialize", "Main view model initialization started.");
        settings = await settingsStore.LoadAsync().ConfigureAwait(true);
        ModelOverride = settings.LastModelOverride ?? string.Empty;
        ReasoningEffortOverride = settings.LastReasoningEffortOverride ?? string.Empty;
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
        activeThreadLoaded = false;
        RestorePersistedThreadState();
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
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

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
            activeThreadId = await EnsureActiveThreadAsync(client).ConfigureAwait(true);

            var submittedPrompt = PromptText.Trim();
            settings.LastModelOverride = NormalizeOverride(ModelOverride);
            settings.LastReasoningEffortOverride = NormalizeOverride(ReasoningEffortOverride);
            var turn = await client.StartTurnAsync(new CodexTurnStartRequest(
                activeThreadId,
                submittedPrompt,
                SelectedProjectPath,
                CodexSandbox.WorkspaceWrite,
                NormalizeOverride(ModelOverride),
                ParseReasoningEffort(ReasoningEffortOverride))).ConfigureAwait(true);

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

    private async Task<string> EnsureActiveThreadAsync(CodexAppServerClient client)
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            throw new InvalidOperationException("A project must be selected before starting a Codex thread.");
        }

        if (string.IsNullOrWhiteSpace(activeThreadId))
        {
            var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
            activeThreadLoaded = true;
            return thread.ThreadId;
        }

        if (activeThreadLoaded)
        {
            return activeThreadId;
        }

        try
        {
            var resumed = await client.ResumeThreadAsync(new CodexThreadResumeRequest(
                activeThreadId,
                SelectedProjectPath,
                CodexSandbox.WorkspaceWrite)).ConfigureAwait(true);
            activeThreadLoaded = true;
            StatusMessage = "Codex thread resumed";
            return resumed.ThreadId;
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "codex_thread_resume_failed",
                "Could not resume persisted Codex thread; starting a new thread.",
                new Dictionary<string, string?> { ["threadId"] = activeThreadId },
                ex);

            var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default).ConfigureAwait(true);
            activeThreadLoaded = true;
            StatusMessage = "Previous thread could not be resumed; started a new Codex thread";
            return thread.ThreadId;
        }
    }

    private async Task CancelTurnAsync()
    {
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

        if (!CanCancelTurn() || appServerClient is null || string.IsNullOrWhiteSpace(activeThreadId) || string.IsNullOrWhiteSpace(activeTurnId))
        {
            StatusMessage = "No active turn to cancel";
            return;
        }

        try
        {
            await appServerClient.CancelTurnAsync(activeThreadId, activeTurnId).ConfigureAwait(true);
            IsTurnRunning = false;
            StatusMessage = "Cancellation requested";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Error, "codex_turn_cancel_failed", "Could not cancel Codex turn.", exception: ex);
        }
    }

    private Task RequestApplicationExitAsync()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        return shutdownTask;
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        if (IsShuttingDown)
        {
            return;
        }

        IsShuttingDown = true;
        StatusMessage = "Closing application";

        await TryCancelRunningTurnForShutdownAsync(cancellationToken).ConfigureAwait(true);
        await SaveActiveThreadStateAsync().ConfigureAwait(true);
        await DisposeAppServerClientAsync().ConfigureAwait(true);

        IsTurnRunning = false;
        activeTurnId = null;
        StatusMessage = "Application closed";
    }

    private async Task TryCancelRunningTurnForShutdownAsync(CancellationToken cancellationToken)
    {
        if (!IsTurnRunning || appServerClient is null || string.IsNullOrWhiteSpace(activeThreadId) || string.IsNullOrWhiteSpace(activeTurnId))
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await appServerClient.CancelTurnAsync(activeThreadId, activeTurnId, timeout.Token).ConfigureAwait(true);
            IsTurnRunning = false;
            StatusMessage = "Cancellation requested";
        }
        catch (OperationCanceledException ex)
        {
            logger.Log(AppLogLevel.Warning, "shutdown_cancel_turn_timed_out", "Timed out while cancelling the active turn during shutdown.", exception: ex);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "shutdown_cancel_turn_failed", "Could not cancel the active turn during shutdown.", exception: ex);
        }
    }

    private async Task LoadModelOptionsAsync()
    {
        if (IsShuttingDown)
        {
            StatusMessage = "Application is closing";
            return;
        }

        if (!currentCodex.IsFound)
        {
            StatusMessage = "Install Codex CLI before loading models";
            return;
        }

        if (currentAuth.Readiness is AuthReadiness.Unavailable or AuthReadiness.NotSignedIn)
        {
            StatusMessage = "Sign in with Codex before loading models";
            return;
        }

        loadModelsCommand.RaiseCanExecuteChanged();
        StatusMessage = "Loading Codex models";

        try
        {
            var client = await EnsureAppServerClientAsync().ConfigureAwait(true);
            var models = await client.ListModelsAsync().ConfigureAwait(true);
            ModelOptions.Clear();
            foreach (var model in models.Select(model => model.Model).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(model => model))
            {
                ModelOptions.Add(model);
            }

            StatusMessage = ModelOptions.Count == 0
                ? "No Codex models returned"
                : $"Loaded {ModelOptions.Count} Codex models";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Log(AppLogLevel.Warning, "codex_model_list_failed", "Could not load Codex model list.", exception: ex);
        }
    }

    private static string? NormalizeOverride(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static CodexReasoningEffort? ParseReasoningEffort(string? value)
    {
        return NormalizeOverride(value)?.ToLowerInvariant() switch
        {
            "none" => CodexReasoningEffort.None,
            "minimal" => CodexReasoningEffort.Minimal,
            "low" => CodexReasoningEffort.Low,
            "medium" => CodexReasoningEffort.Medium,
            "high" => CodexReasoningEffort.High,
            "xhigh" => CodexReasoningEffort.XHigh,
            _ => null
        };
    }

    private bool CanCancelTurn()
    {
        return !IsShuttingDown &&
            IsTurnRunning &&
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

            _ = SaveActiveThreadStateAsync();
        }

        OnPropertyChanged(nameof(FinalResponse));
    }

    private void RestorePersistedThreadState()
    {
        var persisted = FindProjectThreadState();
        if (persisted is null || string.IsNullOrWhiteSpace(persisted.ThreadId))
        {
            threadService.Reset();
            return;
        }

        activeThreadId = persisted.ThreadId;
        threadService.Restore(
            persisted.ThreadId,
            persisted.FinalResponse,
            persisted.TimelineItems,
            persisted.RawEvents);
    }

    private ProjectThreadState? FindProjectThreadState()
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            return null;
        }

        return settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(Path.GetFullPath(thread.ProjectPath), SelectedProjectPath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SaveActiveThreadStateAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProjectPath) || string.IsNullOrWhiteSpace(activeThreadId))
        {
            return;
        }

        try
        {
            var persisted = FindProjectThreadState();
            if (persisted is null)
            {
                persisted = new ProjectThreadState
                {
                    ProjectPath = SelectedProjectPath
                };
                settings.ProjectThreads.Add(persisted);
            }

            persisted.ProjectPath = SelectedProjectPath;
            persisted.ThreadId = activeThreadId;
            persisted.FinalResponse = threadService.FinalResponse;
            persisted.TimelineItems = [.. threadService.TimelineItems.TakeLast(100)];
            persisted.RawEvents = [.. threadService.RawEvents.TakeLast(100)];
            persisted.UpdatedAt = DateTimeOffset.UtcNow;

            await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "thread_state_save_failed", "Could not persist thread state.", exception: ex);
        }
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
        await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task DisposeAppServerClientAsync()
    {
        if (appServerClient is null)
        {
            return;
        }

        appServerClient.NotificationReceived -= OnAppServerNotificationReceived;
        await appServerClient.DisposeAsync().ConfigureAwait(false);
        appServerClient = null;
    }
}
