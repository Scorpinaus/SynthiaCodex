using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ISettingsStore settingsStore;
    private readonly ICodexDiscoveryService codexDiscoveryService;
    private readonly IAuthService authService;
    private readonly IRecentProjectService recentProjectService;
    private readonly IFolderPicker folderPicker;
    private readonly IAppLogger logger;

    private AppSettings settings = new();
    private CodexInstallation currentCodex = CodexInstallation.Missing("Detection has not run yet.");
    private AuthenticationState currentAuth = new(AuthReadiness.Unknown, "Checking sign-in", "Authentication detection has not run yet.", null);
    private string? selectedProjectPath;
    private string statusMessage = "Starting";
    private bool isBusy;

    public MainViewModel(
        ISettingsStore settingsStore,
        ICodexDiscoveryService codexDiscoveryService,
        IAuthService authService,
        IRecentProjectService recentProjectService,
        IFolderPicker folderPicker,
        IAppLogger logger)
    {
        this.settingsStore = settingsStore;
        this.codexDiscoveryService = codexDiscoveryService;
        this.authService = authService;
        this.recentProjectService = recentProjectService;
        this.folderPicker = folderPicker;
        this.logger = logger;

        BrowseProjectCommand = new AsyncRelayCommand(BrowseProjectAsync);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync);
        OpenRecentProjectCommand = new AsyncRelayCommand(OpenRecentProjectAsync);
        SignInChatGptCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.ChatGpt));
        SignInDeviceCodeCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.DeviceCode));
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
    }

    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ICommand BrowseProjectCommand { get; }

    public ICommand RefreshDiagnosticsCommand { get; }

    public ICommand OpenRecentProjectCommand { get; }

    public ICommand SignInChatGptCommand { get; }

    public ICommand SignInDeviceCodeCommand { get; }

    public ICommand SignOutCommand { get; }

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
}
