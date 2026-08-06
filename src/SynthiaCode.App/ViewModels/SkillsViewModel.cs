using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Logging;
using SynthiaCode.Harnesses.Codex;

namespace SynthiaCode.App.ViewModels;

public sealed class SkillItemViewModel : ObservableObject
{
    private CodexSkillMetadata metadata;
    private bool isEnabled;
    private bool isBusy;

    public SkillItemViewModel(CodexSkillMetadata metadata)
    {
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        isEnabled = metadata.Enabled;
    }

    public string Name => metadata.Name;

    public string DisplayName =>
        FirstNonEmpty(metadata.Interface?.DisplayName, metadata.Name);

    public string Description =>
        FirstNonEmpty(
            metadata.Interface?.ShortDescription,
            metadata.ShortDescription,
            metadata.Description);

    public string Path => metadata.Path;

    public CodexSkillScope Scope => metadata.Scope;

    public string ScopeLabel => metadata.Scope.ToString();

    public string DependenciesSummary =>
        metadata.Dependencies?.Tools.Count > 0
            ? string.Join(
                ", ",
                metadata.Dependencies.Tools.Select(tool =>
                    string.IsNullOrWhiteSpace(tool.Value)
                        ? tool.Type
                        : $"{tool.Type}: {tool.Value}"))
            : "No declared tool dependencies";

    public bool IsEnabled
    {
        get => isEnabled;
        private set
        {
            if (SetProperty(ref isEnabled, value))
            {
                OnPropertyChanged(nameof(ToggleActionLabel));
                OnPropertyChanged(nameof(ToggleAutomationName));
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public string StateLabel => IsEnabled ? "Enabled" : "Disabled";

    public string ToggleActionLabel => IsEnabled ? "Disable" : "Enable";

    public string ToggleAutomationName => $"{ToggleActionLabel} {DisplayName}";

    internal CodexSkillMetadata Metadata => metadata;

    internal bool Matches(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return new[]
        {
            DisplayName,
            Name,
            Description,
            Path,
            ScopeLabel,
            DependenciesSummary
        }.Any(value => value.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    internal void UpdateFrom(CodexSkillMetadata value)
    {
        metadata = value;
        IsEnabled = value.Enabled;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(DependenciesSummary));
        OnPropertyChanged(nameof(ToggleAutomationName));
    }

    internal void SetEnabled(bool value) => IsEnabled = value;

    internal void SetBusy(bool value) => IsBusy = value;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unnamed skill";
}

public sealed class SkillsViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly ICodexSkillsSessionFeature coordinator;
    private readonly Func<string?> activeWorkspacePath;
    private readonly Func<string> activeContextLabel;
    private readonly Action<string> openInEditor;
    private readonly Action<string> revealInExplorer;
    private readonly Func<bool> isShuttingDown;
    private readonly Action<string> reportStatus;
    private readonly IAppLogger logger;
    private readonly SynchronizationContext? synchronizationContext;
    private readonly AsyncRelayCommand refreshCommand;
    private readonly AsyncRelayCommand toggleSkillCommand;
    private readonly RelayCommand openSkillCommand;
    private readonly RelayCommand revealSkillCommand;
    private readonly List<SkillItemViewModel> allSkills = [];
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? invalidationCancellation;
    private string searchText = string.Empty;
    private string selectedScopeFilter = "All";
    private string contextPath = string.Empty;
    private string contextLabel = "No active workspace";
    private string message = "Open Settings to load Codex skills.";
    private int enabledCount;
    private int errorCount;
    private bool isBusy;
    private bool isStale = true;
    private bool isActive;
    private bool isSupported = true;
    private bool canWrite = true;
    private bool disposed;
    private long refreshGeneration;

    public SkillsViewModel(
        ICodexSkillsSessionFeature coordinator,
        Func<string?> activeWorkspacePath,
        Func<string> activeContextLabel,
        Action<string> openInEditor,
        Action<string> revealInExplorer,
        Func<bool> isShuttingDown,
        Action<string> reportStatus,
        IAppLogger logger)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.activeWorkspacePath = activeWorkspacePath ?? throw new ArgumentNullException(nameof(activeWorkspacePath));
        this.activeContextLabel = activeContextLabel ?? throw new ArgumentNullException(nameof(activeContextLabel));
        this.openInEditor = openInEditor ?? throw new ArgumentNullException(nameof(openInEditor));
        this.revealInExplorer = revealInExplorer ?? throw new ArgumentNullException(nameof(revealInExplorer));
        this.isShuttingDown = isShuttingDown ?? throw new ArgumentNullException(nameof(isShuttingDown));
        this.reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        synchronizationContext = SynchronizationContext.Current;

        RefreshCommand = refreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(forceReload: true),
            CanRefresh);
        ToggleSkillCommand = toggleSkillCommand = new AsyncRelayCommand(
            parameter => parameter is SkillItemViewModel item
                ? ToggleSkillAsync(item)
                : Task.CompletedTask,
            CanToggle);
        OpenSkillCommand = openSkillCommand = new RelayCommand(OpenSkill, CanOpenOrReveal);
        RevealSkillCommand = revealSkillCommand = new RelayCommand(RevealSkill, CanOpenOrReveal);

        coordinator.NotificationReceived += OnNotificationReceived;
        coordinator.StateChanged += OnStateChanged;
    }

    public ObservableCollection<SkillItemViewModel> Skills { get; } = [];

    public IReadOnlyList<string> ScopeFilters { get; } =
        ["All", "User", "Repository", "System", "Admin", "Unknown"];

    public ICommand RefreshCommand { get; }

    public ICommand ToggleSkillCommand { get; }

    public ICommand OpenSkillCommand { get; }

    public ICommand RevealSkillCommand { get; }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedScopeFilter
    {
        get => selectedScopeFilter;
        set
        {
            var normalized = ScopeFilters.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? ScopeFilters.First(option => option.Equals(value, StringComparison.OrdinalIgnoreCase))
                : "All";
            if (SetProperty(ref selectedScopeFilter, normalized))
            {
                ApplyFilter();
            }
        }
    }

    public string ContextPath
    {
        get => contextPath;
        private set => SetProperty(ref contextPath, value);
    }

    public string ContextLabel
    {
        get => contextLabel;
        private set => SetProperty(ref contextLabel, value);
    }

    public string Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public int EnabledCount
    {
        get => enabledCount;
        private set => SetProperty(ref enabledCount, value);
    }

    public int DisabledCount => Math.Max(0, allSkills.Count - EnabledCount);

    public int TotalCount => allSkills.Count;

    public int ErrorCount
    {
        get => errorCount;
        private set => SetProperty(ref errorCount, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsStale
    {
        get => isStale;
        private set => SetProperty(ref isStale, value);
    }

    public bool IsSupported
    {
        get => isSupported;
        private set => SetProperty(ref isSupported, value);
    }

    public bool CanWrite
    {
        get => canWrite;
        private set
        {
            if (SetProperty(ref canWrite, value))
            {
                toggleSkillCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsActive
    {
        get => isActive;
        set
        {
            if (SetProperty(ref isActive, value) && value && IsStale)
            {
                _ = RefreshAsync(forceReload: false);
            }
        }
    }

    public async Task RefreshAsync(
        bool forceReload = true,
        CancellationToken cancellationToken = default)
    {
        if (disposed || isShuttingDown())
        {
            return;
        }

        UpdateContext();
        if (string.IsNullOrWhiteSpace(ContextPath))
        {
            allSkills.Clear();
            ApplyFilter();
            IsStale = true;
            Message = "Select a project or task workspace to inspect Codex skills.";
            return;
        }

        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = refreshCancellation.Token;
        var generation = Interlocked.Increment(ref refreshGeneration);
        var requestedPath = ContextPath;

        IsBusy = true;
        Message = forceReload ? "Reloading Codex skills..." : "Loading Codex skills...";
        try
        {
            var result = await coordinator
                .ListSkillsAsync(new CodexSkillListRequest([requestedPath], forceReload), token)
                .ConfigureAwait(true);
            if (generation != Volatile.Read(ref refreshGeneration) ||
                !PathComparer.Equals(requestedPath, NormalizePath(activeWorkspacePath())))
            {
                return;
            }

            IsSupported = result.IsSupported;
            CanWrite = result.IsSupported;
            if (!result.IsSupported)
            {
                allSkills.Clear();
                ApplyFilter();
                ErrorCount = 0;
                IsStale = false;
                Message = "This Codex app-server version does not expose skill discovery.";
                return;
            }

            var context = result.Contexts.FirstOrDefault(item => PathComparer.Equals(
                NormalizePath(item.Cwd),
                requestedPath));
            ApplyResult(context);
            IsStale = false;
            reportStatus("Codex skills refreshed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            IsStale = true;
            Message = $"Codex skills could not be loaded: {ex.Message}";
            logger.Log(
                AppLogLevel.Warning,
                "codex_skills_refresh_failed",
                "Codex skills could not be refreshed.",
                exception: ex);
        }
        finally
        {
            if (generation == Volatile.Read(ref refreshGeneration))
            {
                IsBusy = false;
            }
        }
    }

    public async Task ToggleSkillAsync(
        SkillItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanToggle(item))
        {
            return;
        }

        var previous = item.IsEnabled;
        item.SetBusy(true);
        RaiseCommandStates();
        try
        {
            var result = await coordinator
                .WriteSkillConfigAsync(
                    new CodexSkillConfigWriteRequest(item.Path, !previous),
                    cancellationToken)
                .ConfigureAwait(true);
            if (!result.IsSupported)
            {
                CanWrite = false;
                Message = "This Codex app-server version does not support changing skill enablement.";
                return;
            }

            item.SetEnabled(result.EffectiveEnabled);
            Message = $"{item.DisplayName} is now {(result.EffectiveEnabled ? "enabled" : "disabled")}.";
            reportStatus(Message);
            await RefreshAsync(forceReload: true, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.SetEnabled(previous);
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            item.SetEnabled(previous);
            Message = $"Skill enablement could not be changed: {ex.Message}";
            logger.Log(
                AppLogLevel.Warning,
                "codex_skill_toggle_failed",
                "Codex skill enablement could not be changed.",
                new Dictionary<string, string?> { ["path"] = item.Path },
                ex);
        }
        finally
        {
            item.SetBusy(false);
            RaiseCommandStates();
        }
    }

    public void NotifyContextChanged()
    {
        UpdateContext();
        IsStale = true;
        if (IsActive)
        {
            _ = RefreshAsync(forceReload: false);
        }
    }

    public IReadOnlyList<CodexSkillMetadata> GetEnabledSkillSnapshot() =>
        allSkills
            .Where(item => item.IsEnabled)
            .Select(item => item.Metadata)
            .ToList();

    public void RaiseCommandStates() =>
        RaiseCommandStatesCore();

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        coordinator.NotificationReceived -= OnNotificationReceived;
        coordinator.StateChanged -= OnStateChanged;
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        invalidationCancellation?.Cancel();
        invalidationCancellation?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ApplyResult(CodexSkillContextResult? context)
    {
        var existing = allSkills.ToDictionary(item => item.Path, PathComparer);
        var updated = new List<SkillItemViewModel>();
        foreach (var metadata in context?.Skills ?? [])
        {
            if (existing.TryGetValue(metadata.Path, out var item))
            {
                item.UpdateFrom(metadata);
            }
            else
            {
                item = new SkillItemViewModel(metadata);
            }

            updated.Add(item);
        }

        allSkills.Clear();
        allSkills.AddRange(updated.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase));
        EnabledCount = allSkills.Count(item => item.IsEnabled);
        ErrorCount = context?.Errors.Count ?? 0;
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(TotalCount));
        ApplyFilter();

        Message = ErrorCount switch
        {
            0 when allSkills.Count == 0 => "No Codex skills were discovered for this context.",
            0 => $"Loaded {allSkills.Count} skill(s): {EnabledCount} enabled, {DisabledCount} disabled.",
            _ => $"Loaded {allSkills.Count} skill(s) with {ErrorCount} discovery error(s)."
        };
    }

    private void ApplyFilter()
    {
        var matches = allSkills.Where(item =>
            (SelectedScopeFilter == "All" ||
             item.ScopeLabel.Equals(SelectedScopeFilter, StringComparison.OrdinalIgnoreCase)) &&
            item.Matches(SearchText));

        Skills.Clear();
        foreach (var item in matches)
        {
            Skills.Add(item);
        }
    }

    private void UpdateContext()
    {
        ContextPath = NormalizePath(activeWorkspacePath());
        ContextLabel = string.IsNullOrWhiteSpace(ContextPath)
            ? "No active workspace"
            : activeContextLabel();
    }

    private void OnNotificationReceived(object? sender, CodexAppServerNotification notification)
    {
        if (notification.Kind != CodexAppServerNotificationKind.SkillsChanged)
        {
            return;
        }

        Dispatch(() =>
        {
            IsStale = true;
            invalidationCancellation?.Cancel();
            invalidationCancellation?.Dispose();
            invalidationCancellation = new CancellationTokenSource();
            _ = RefreshAfterInvalidationAsync(invalidationCancellation.Token);
        });
    }

    private async Task RefreshAfterInvalidationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (IsActive)
            {
                await DispatchAsync(() => RefreshAsync(forceReload: false, cancellationToken))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnStateChanged(object? sender, AppServerSessionStateChangedEventArgs args)
    {
        Dispatch(() =>
        {
            if (args.State != AppServerSessionState.Connected)
            {
                IsStale = true;
                return;
            }

            if (IsActive && IsStale)
            {
                _ = RefreshAsync(forceReload: false);
            }
        });
    }

    private void OpenSkill(object? parameter)
    {
        if (parameter is not SkillItemViewModel item)
        {
            return;
        }

        try
        {
            openInEditor(item.Path);
            Message = $"Opened {item.DisplayName} in the configured editor.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
        }
    }

    private void RevealSkill(object? parameter)
    {
        if (parameter is not SkillItemViewModel item)
        {
            return;
        }

        try
        {
            revealInExplorer(item.Path);
            Message = $"Revealed {item.DisplayName} in Explorer.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
        }
    }

    private bool CanRefresh() => !disposed && !isShuttingDown() && !IsBusy;

    private bool CanToggle(object? parameter) =>
        !disposed &&
        !isShuttingDown() &&
        !IsBusy &&
        CanWrite &&
        parameter is SkillItemViewModel item &&
        !item.IsBusy;

    private bool CanOpenOrReveal(object? parameter) =>
        !disposed &&
        !isShuttingDown() &&
        parameter is SkillItemViewModel item &&
        Path.IsPathRooted(item.Path);

    private void RaiseCommandStatesCore()
    {
        refreshCommand.RaiseCanExecuteChanged();
        toggleSkillCommand.RaiseCanExecuteChanged();
        openSkillCommand.RaiseCanExecuteChanged();
        revealSkillCommand.RaiseCanExecuteChanged();
    }

    private void Dispatch(Action action)
    {
        if (synchronizationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private Task DispatchAsync(Func<Task> action)
    {
        if (synchronizationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synchronizationContext.Post(
            async _ =>
            {
                try
                {
                    await action().ConfigureAwait(true);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            null);
        return completion.Task;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
