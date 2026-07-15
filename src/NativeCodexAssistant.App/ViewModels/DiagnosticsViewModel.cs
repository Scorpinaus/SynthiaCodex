using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject
{
    public const int MaximumDiagnosticLines = 500;
    private readonly ICodexDiscoveryService discoveryService;
    private readonly IAuthService authService;
    private readonly ICodexCliUtilityRunner utilityRunner;
    private readonly IAppLogger logger;
    private readonly Func<string?> preferredCodexPath;
    private readonly Func<bool> isShuttingDown;
    private readonly Action<string> reportStatus;
    private readonly string settingsPath;
    private readonly AsyncRelayCommand refreshCommand;
    private readonly AsyncRelayCommand runDoctorCommand;
    private CodexInstallation installation = CodexInstallation.Missing("Detection has not run yet.");
    private AuthenticationState authentication = new(AuthReadiness.Unknown, "Checking sign-in", "Authentication detection has not run yet.", null);
    private CodexCliUtilityResult? doctorResult;
    private bool isBusy;

    public DiagnosticsViewModel(
        ICodexDiscoveryService discoveryService,
        IAuthService authService,
        ICodexCliUtilityRunner utilityRunner,
        IAppLogger logger,
        Func<string?> preferredCodexPath,
        Func<bool> isShuttingDown,
        Action<string> reportStatus,
        string settingsPath)
    {
        this.discoveryService = discoveryService;
        this.authService = authService;
        this.utilityRunner = utilityRunner;
        this.logger = logger;
        this.preferredCodexPath = preferredCodexPath;
        this.isShuttingDown = isShuttingDown;
        this.reportStatus = reportStatus;
        this.settingsPath = settingsPath;
        RefreshCommand = refreshCommand = new AsyncRelayCommand(RefreshAsync, () => !isShuttingDown() && !IsBusy);
        RunDoctorCommand = runDoctorCommand = new AsyncRelayCommand(RunDoctorAsync, CanRunDoctor);
        SignInChatGptCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.ChatGpt));
        SignInDeviceCodeCommand = new AsyncRelayCommand(() => StartLoginAsync(LoginMethod.DeviceCode));
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        RefreshLines();
    }

    public event EventHandler? EnvironmentChanged;

    public ObservableCollection<string> Lines { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand RunDoctorCommand { get; }

    public ICommand SignInChatGptCommand { get; }

    public ICommand SignInDeviceCodeCommand { get; }

    public ICommand SignOutCommand { get; }

    public CodexInstallation Installation => installation;

    public AuthenticationState Authentication => authentication;

    public string CodexSummary => installation.Summary;

    public string CodexExecutablePath => installation.ExecutablePath ?? "Not found";

    public string CodexVersion => installation.Version ?? "Unknown";

    public string AuthSummary => authentication.Summary;

    public string AuthDetail => authentication.Detail;

    public string CodexHome => authentication.CodexHome ?? "Default not resolved";

    public string SettingsPath => settingsPath;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                refreshCommand.RaiseCanExecuteChanged();
                runDoctorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        reportStatus("Refreshing diagnostics");
        try
        {
            installation = await discoveryService.DetectAsync(preferredCodexPath()).ConfigureAwait(true);
            authentication = await authService.GetAuthenticationStateAsync(installation).ConfigureAwait(true);
            RefreshLines();
            RaiseComputedProperties();
            EnvironmentChanged?.Invoke(this, EventArgs.Empty);
            reportStatus("Diagnostics refreshed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RaiseCommandStates()
    {
        refreshCommand.RaiseCanExecuteChanged();
        runDoctorCommand.RaiseCanExecuteChanged();
    }

    private async Task RunDoctorAsync()
    {
        if (!CanRunDoctor())
        {
            reportStatus("Codex doctor is unavailable");
            return;
        }

        IsBusy = true;
        reportStatus("Running Codex doctor");
        try
        {
            doctorResult = await utilityRunner.RunDoctorAsync(installation).ConfigureAwait(true);
            RefreshLines();
            reportStatus(doctorResult.Succeeded
                ? "Codex doctor completed"
                : $"Codex doctor failed with exit code {doctorResult.ExitCode}");
        }
        catch (Exception ex)
        {
            reportStatus(ex.Message);
            logger.Log(AppLogLevel.Error, "codex_doctor_failed", "Could not run codex doctor.", exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunDoctor() => !isShuttingDown() && !IsBusy && installation.IsFound;

    private async Task StartLoginAsync(LoginMethod method)
    {
        if (!installation.IsFound)
        {
            reportStatus("Install Codex CLI before signing in");
            return;
        }

        var started = await authService.StartLoginAsync(installation, method).ConfigureAwait(true);
        reportStatus(started ? "Sign-in opened in a terminal window" : "Could not start sign-in");
    }

    private async Task SignOutAsync()
    {
        if (!installation.IsFound)
        {
            reportStatus("Codex CLI is not available");
            return;
        }

        var started = await authService.StartLogoutAsync(installation).ConfigureAwait(true);
        reportStatus(started ? "Sign-out opened in a terminal window" : "Could not start sign-out");
    }

    private void RefreshLines()
    {
        Lines.Clear();
        AddLine($"App: {Assembly.GetExecutingAssembly().GetName().Version}");
        AddLine($"Windows: {Environment.OSVersion.VersionString}");
        AddLine($".NET: {Environment.Version}");
        AddLine($"Codex: {CodexSummary}");
        AddLine($"Codex path: {CodexExecutablePath}");
        AddLine($"Codex version: {CodexVersion}");
        AddLine($"Sign-in: {AuthSummary}");
        AddLine($"Codex home: {CodexHome}");
        AddLine($"Settings: {SettingsPath}");
        if (doctorResult is null)
        {
            return;
        }

        AddLine($"Codex doctor exit code: {doctorResult.ExitCode}");
        foreach (var line in SplitLines(doctorResult.StandardOutput))
        {
            AddLine($"Doctor: {line}");
        }

        foreach (var line in SplitLines(doctorResult.StandardError))
        {
            AddLine($"Doctor stderr: {line}");
        }
    }

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(CodexSummary));
        OnPropertyChanged(nameof(CodexExecutablePath));
        OnPropertyChanged(nameof(CodexVersion));
        OnPropertyChanged(nameof(AuthSummary));
        OnPropertyChanged(nameof(AuthDetail));
        OnPropertyChanged(nameof(CodexHome));
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void AddLine(string line)
    {
        Lines.Add(line);
        if (Lines.Count > MaximumDiagnosticLines)
        {
            Lines.RemoveAt(0);
        }
    }
}
