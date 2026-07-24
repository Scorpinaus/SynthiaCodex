using System.Windows.Input;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Terminal;

namespace SynthiaCode.App.ViewModels;

public sealed record TerminalContext(string? Key, string? WorkspacePath);

public sealed class TerminalViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan PresentationInterval = TimeSpan.FromMilliseconds(50);

    private readonly ITerminalService terminalService;
    private readonly IAppLogger logger;
    private readonly Func<TerminalContext> contextProvider;
    private readonly Func<bool> isShuttingDown;
    private readonly Action<string> reportStatus;
    private readonly Action requestWorkspaceSelection;
    private readonly SynchronizationContext? synchronizationContext;
    private readonly Dictionary<string, TerminalState> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncRelayCommand startCommand;
    private readonly AsyncRelayCommand sendInputCommand;
    private readonly AsyncRelayCommand killCommand;
    private readonly RelayCommand clearCommand;
    private readonly RelayCommand toggleCommand;
    private readonly RelayCommand toggleMaximizeCommand;
    private string input = string.Empty;
    private string output = string.Empty;
    private string status = "Not started";
    private string workingDirectory = "No terminal session";
    private bool isVisible;
    private bool isMaximized;

    public TerminalViewModel(
        ITerminalService terminalService,
        IAppLogger logger,
        Func<TerminalContext> contextProvider,
        Func<bool> isShuttingDown,
        Action<string> reportStatus,
        Action requestWorkspaceSelection)
    {
        this.terminalService = terminalService;
        this.logger = logger;
        this.contextProvider = contextProvider;
        this.isShuttingDown = isShuttingDown;
        this.reportStatus = reportStatus;
        this.requestWorkspaceSelection = requestWorkspaceSelection;
        synchronizationContext = SynchronizationContext.Current;
        StartCommand = startCommand = new AsyncRelayCommand(StartAsync, CanStart);
        SendInputCommand = sendInputCommand = new AsyncRelayCommand(SendInputAsync, CanSendInput);
        KillCommand = killCommand = new AsyncRelayCommand(KillAsync, CanKill);
        ClearCommand = clearCommand = new RelayCommand(Clear, CanClear);
        ToggleCommand = toggleCommand = new RelayCommand(Toggle, () => !isShuttingDown());
        ToggleMaximizeCommand = toggleMaximizeCommand = new RelayCommand(
            ToggleMaximize,
            () => !isShuttingDown() && IsVisible);
    }

    public ICommand StartCommand { get; }

    public ICommand SendInputCommand { get; }

    public ICommand KillCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand ToggleCommand { get; }

    public ICommand ToggleMaximizeCommand { get; }

    public string Input
    {
        get => input;
        set
        {
            if (SetProperty(ref input, value))
            {
                sendInputCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Output
    {
        get => output;
        private set => SetProperty(ref output, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string WorkingDirectory
    {
        get => workingDirectory;
        private set => SetProperty(ref workingDirectory, value);
    }

    public bool IsRunning => GetCurrentState()?.Session.IsRunning == true;

    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (SetProperty(ref isVisible, value))
            {
                if (!value)
                {
                    IsMaximized = false;
                }

                toggleMaximizeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsMaximized
    {
        get => isMaximized;
        set => SetProperty(ref isMaximized, value);
    }

    public int SessionCount => states.Count;

    public void RefreshContext()
    {
        var context = contextProvider();
        var state = GetCurrentState();
        Output = state?.Output.Snapshot() ?? string.Empty;
        WorkingDirectory = state?.Session.WorkingDirectory
            ?? context.WorkspacePath
            ?? "No terminal session";
        Status = state is null
            ? "Not started"
            : state.Session.IsRunning ? "Running" : $"Exited ({state.ExitCode ?? 0})";
        OnPropertyChanged(nameof(IsRunning));
        RaiseCommandStates();
    }

    public async Task StopAndRemoveAsync(string key)
    {
        if (!states.Remove(key, out var state))
        {
            return;
        }

        await state.Session.DisposeAsync().ConfigureAwait(true);
        LogMetrics(key, state);
        RefreshContext();
    }

    public async Task ShutdownAsync()
    {
        var activeStates = states.ToArray();
        states.Clear();
        foreach (var (key, state) in activeStates)
        {
            try
            {
                await state.Session.DisposeAsync().ConfigureAwait(true);
                LogMetrics(key, state);
            }
            catch (Exception ex)
            {
                logger.Log(AppLogLevel.Warning, "integrated_terminal_dispose_failed", "Could not dispose an integrated terminal session.", exception: ex);
            }
        }

        RefreshContext();
    }

    public void RaiseCommandStates()
    {
        startCommand.RaiseCanExecuteChanged();
        sendInputCommand.RaiseCanExecuteChanged();
        killCommand.RaiseCanExecuteChanged();
        clearCommand.RaiseCanExecuteChanged();
        toggleCommand.RaiseCanExecuteChanged();
        toggleMaximizeCommand.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private async Task StartAsync()
    {
        if (!CanStart())
        {
            return;
        }

        var context = contextProvider();
        var key = context.Key!;
        var workspace = context.WorkspacePath!;
        try
        {
            if (states.Remove(key, out var previous))
            {
                await previous.Session.DisposeAsync().ConfigureAwait(true);
                LogMetrics(key, previous);
            }

            var session = await terminalService.StartSessionAsync(new TerminalStartRequest(workspace, 120, 30)).ConfigureAwait(true);
            var state = new TerminalState(session);
            states[key] = state;
            session.OutputReceived += (_, args) => HandleOutput(key, state, args.Text);
            session.Exited += (_, args) => HandleExited(key, args.ExitCode);
            IsVisible = true;
            requestWorkspaceSelection();
            RefreshContext();
            reportStatus("PowerShell terminal started");
            logger.Log(
                AppLogLevel.Information,
                "integrated_terminal_started",
                "An integrated terminal session was started.",
                new Dictionary<string, string?> { ["key"] = key, ["workingDirectory"] = workspace });
        }
        catch (Exception ex)
        {
            Status = "Start failed";
            reportStatus(ex.Message);
            logger.Log(AppLogLevel.Error, "integrated_terminal_start_failed", "Could not start an integrated terminal.", exception: ex);
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    private async Task SendInputAsync()
    {
        var state = GetCurrentState();
        if (state?.Session.IsRunning != true || string.IsNullOrWhiteSpace(Input))
        {
            return;
        }

        var value = Input;
        try
        {
            await state.Session.WriteInputAsync(value + "\r\n").ConfigureAwait(true);
            Input = string.Empty;
            reportStatus("Terminal input sent");
        }
        catch (Exception ex)
        {
            reportStatus(ex.Message);
            logger.Log(AppLogLevel.Warning, "integrated_terminal_input_failed", "Could not write to the integrated terminal.", exception: ex);
        }
    }

    private void Clear()
    {
        var state = GetCurrentState();
        if (state is null)
        {
            return;
        }

        state.Output.Clear();
        Output = string.Empty;
        clearCommand.RaiseCanExecuteChanged();
        reportStatus("Terminal output cleared");
    }

    private async Task KillAsync()
    {
        var state = GetCurrentState();
        if (state?.Session.IsRunning != true)
        {
            return;
        }

        try
        {
            await state.Session.StopAsync().ConfigureAwait(true);
            Status = "Exited";
            reportStatus("Terminal session stopped");
        }
        catch (Exception ex)
        {
            reportStatus(ex.Message);
            logger.Log(AppLogLevel.Warning, "integrated_terminal_stop_failed", "Could not stop the integrated terminal.", exception: ex);
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    private void Toggle()
    {
        IsVisible = !IsVisible;
        if (IsVisible)
        {
            requestWorkspaceSelection();
        }

        reportStatus(IsVisible ? "Terminal panel shown" : "Terminal panel hidden");
    }

    private void ToggleMaximize()
    {
        if (!IsVisible)
        {
            return;
        }

        IsMaximized = !IsMaximized;
        reportStatus(IsMaximized ? "Terminal maximized in workspace" : "Terminal restored in workspace");
    }

    private bool CanStart()
    {
        var context = contextProvider();
        return !isShuttingDown() &&
            !string.IsNullOrWhiteSpace(context.Key) &&
            !string.IsNullOrWhiteSpace(context.WorkspacePath) &&
            GetCurrentState()?.Session.IsRunning != true;
    }

    private bool CanSendInput() =>
        !isShuttingDown() && GetCurrentState()?.Session.IsRunning == true && !string.IsNullOrWhiteSpace(Input);

    private bool CanKill() => !isShuttingDown() && GetCurrentState()?.Session.IsRunning == true;

    private bool CanClear() => GetCurrentState()?.Output.Length > 0;

    private TerminalState? GetCurrentState()
    {
        var key = contextProvider().Key;
        return key is not null && states.TryGetValue(key, out var state) ? state : null;
    }

    private void HandleOutput(string key, TerminalState state, string text)
    {
        state.RecordOutput(text);
        if (state.TrySchedulePresentation())
        {
            _ = PublishOutputAsync(key, state);
        }
    }

    private async Task PublishOutputAsync(string key, TerminalState state)
    {
        await Task.Delay(PresentationInterval).ConfigureAwait(false);
        Dispatch(() =>
        {
            state.CompleteScheduledPresentation();
            if (state.MetricsLogged || !states.TryGetValue(key, out var current) || !ReferenceEquals(current, state))
            {
                return;
            }

            if (string.Equals(key, contextProvider().Key, StringComparison.OrdinalIgnoreCase))
            {
                Output = state.Output.Snapshot();
                state.RecordPresentation();
                clearCommand.RaiseCanExecuteChanged();
            }
        });
    }

    private void HandleExited(string key, int exitCode) => Dispatch(() =>
    {
        if (!states.TryGetValue(key, out var state))
        {
            return;
        }

        state.ExitCode = exitCode;
        if (string.Equals(key, contextProvider().Key, StringComparison.OrdinalIgnoreCase))
        {
            var final = state.Output.Snapshot();
            if (!string.Equals(Output, final, StringComparison.Ordinal))
            {
                Output = final;
                state.RecordPresentation();
            }

            Status = $"Exited ({exitCode})";
            OnPropertyChanged(nameof(IsRunning));
            RaiseCommandStates();
        }

        LogMetrics(key, state);
    });

    private void Dispatch(Action action)
    {
        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            action();
        }
        else
        {
            synchronizationContext.Post(_ => action(), null);
        }
    }

    private void LogMetrics(string key, TerminalState state)
    {
        if (!state.TryMarkMetricsLogged())
        {
            return;
        }

        logger.Log(
            AppLogLevel.Information,
            "terminal_output_metrics",
            "Terminal output batching metrics were recorded.",
            new Dictionary<string, string?>
            {
                ["key"] = key,
                ["receivedChunks"] = state.ReceivedChunkCount.ToString(),
                ["receivedCharacters"] = state.ReceivedCharacterCount.ToString(),
                ["presentationUpdates"] = state.PresentationCount.ToString(),
                ["retainedCharacters"] = state.Output.Length.ToString(),
                ["elapsedMilliseconds"] = state.ElapsedMilliseconds.ToString()
            });
    }

    private sealed class TerminalState(ITerminalSession session)
    {
        private readonly long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        private long receivedChunkCount;
        private long receivedCharacterCount;
        private long presentationCount;
        private int presentationScheduled;
        private int metricsLogged;

        public ITerminalSession Session { get; } = session;

        public BoundedTextBuffer Output { get; } = new(250_000);

        public int? ExitCode { get; set; }

        public long ReceivedChunkCount => Volatile.Read(ref receivedChunkCount);

        public long ReceivedCharacterCount => Volatile.Read(ref receivedCharacterCount);

        public long PresentationCount => Volatile.Read(ref presentationCount);

        public long ElapsedMilliseconds => (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

        public bool MetricsLogged => Volatile.Read(ref metricsLogged) != 0;

        public void RecordOutput(string text)
        {
            Output.Append(text);
            Interlocked.Increment(ref receivedChunkCount);
            Interlocked.Add(ref receivedCharacterCount, text.Length);
        }

        public bool TrySchedulePresentation() => Interlocked.CompareExchange(ref presentationScheduled, 1, 0) == 0;

        public void CompleteScheduledPresentation() => Interlocked.Exchange(ref presentationScheduled, 0);

        public void RecordPresentation() => Interlocked.Increment(ref presentationCount);

        public bool TryMarkMetricsLogged() => Interlocked.CompareExchange(ref metricsLogged, 1, 0) == 0;
    }
}
