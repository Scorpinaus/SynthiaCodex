using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.App.ViewModels;

public sealed class CodexConfigurationViewModel : ObservableObject
{
    private readonly ISharedCodexConfigurationService service;
    private readonly Func<string?> activeWorkspacePath;
    private readonly Action<string> openInEditor;
    private readonly Action<string> revealInExplorer;
    private readonly Func<bool> isShuttingDown;
    private readonly Action<string> reportStatus;
    private readonly IAppLogger logger;
    private readonly AsyncRelayCommand refreshCommand;
    private readonly AsyncRelayCommand saveSharedInstructionsCommand;
    private readonly AsyncRelayCommand saveSharedConfigurationCommand;
    private readonly AsyncRelayCommand openSharedInstructionsCommand;
    private readonly AsyncRelayCommand openSharedConfigurationCommand;
    private readonly AsyncRelayCommand openSourceCommand;
    private readonly RelayCommand revealSourceCommand;
    private string sharedInstructionsText = string.Empty;
    private string sharedConfigurationText = string.Empty;
    private string loadedSharedInstructionsText = string.Empty;
    private string loadedSharedConfigurationText = string.Empty;
    private string sharedInstructionsRevision = "missing";
    private string sharedConfigurationRevision = "missing";
    private string configurationMessage = "Refresh to inspect shared Codex configuration.";
    private bool sharedInstructionsExists;
    private bool sharedConfigurationExists;
    private bool isLoaded;
    private bool isBusy;

    public CodexConfigurationViewModel(
        ISharedCodexConfigurationService service,
        Func<string?> activeWorkspacePath,
        Action<string> openInEditor,
        Action<string> revealInExplorer,
        Func<bool> isShuttingDown,
        Action<string> reportStatus,
        IAppLogger logger)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.activeWorkspacePath = activeWorkspacePath ?? throw new ArgumentNullException(nameof(activeWorkspacePath));
        this.openInEditor = openInEditor ?? throw new ArgumentNullException(nameof(openInEditor));
        this.revealInExplorer = revealInExplorer ?? throw new ArgumentNullException(nameof(revealInExplorer));
        this.isShuttingDown = isShuttingDown ?? throw new ArgumentNullException(nameof(isShuttingDown));
        this.reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        RefreshCommand = refreshCommand = new AsyncRelayCommand(() => RefreshAsync(), CanRefresh);
        SaveSharedInstructionsCommand = saveSharedInstructionsCommand =
            new AsyncRelayCommand(() => SaveSharedInstructionsAsync(), CanSaveSharedInstructions);
        SaveSharedConfigurationCommand = saveSharedConfigurationCommand =
            new AsyncRelayCommand(() => SaveSharedConfigurationAsync(), CanSaveSharedConfiguration);
        OpenSharedInstructionsCommand = openSharedInstructionsCommand =
            new AsyncRelayCommand(() => OpenSharedInstructionsAsync(), CanOpen);
        OpenSharedConfigurationCommand = openSharedConfigurationCommand =
            new AsyncRelayCommand(() => OpenSharedConfigurationAsync(), CanOpen);
        OpenSourceCommand = openSourceCommand =
            new AsyncRelayCommand(OpenSourceAsync, CanOpenSource);
        RevealSourceCommand = revealSourceCommand =
            new RelayCommand(RevealSource, CanRevealSource);
    }

    public ObservableCollection<CodexConfigurationSource> Provenance { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand SaveSharedInstructionsCommand { get; }

    public ICommand SaveSharedConfigurationCommand { get; }

    public ICommand OpenSharedInstructionsCommand { get; }

    public ICommand OpenSharedConfigurationCommand { get; }

    public ICommand OpenSourceCommand { get; }

    public ICommand RevealSourceCommand { get; }

    public string SharedInstructionsPath => Path.Combine(service.CodexHomePath, "AGENTS.md");

    public string SharedConfigurationPath => Path.Combine(service.CodexHomePath, "config.toml");

    public string SharedInstructionsText
    {
        get => sharedInstructionsText;
        set
        {
            if (SetProperty(ref sharedInstructionsText, value ?? string.Empty))
            {
                RaiseEditorState();
            }
        }
    }

    public string SharedConfigurationText
    {
        get => sharedConfigurationText;
        set
        {
            if (SetProperty(ref sharedConfigurationText, value ?? string.Empty))
            {
                RaiseEditorState();
            }
        }
    }

    public string ConfigurationMessage
    {
        get => configurationMessage;
        private set => SetProperty(ref configurationMessage, value);
    }

    public bool HasSharedInstructionsChanges =>
        isLoaded &&
        !string.Equals(SharedInstructionsText, loadedSharedInstructionsText, StringComparison.Ordinal);

    public bool HasSharedConfigurationChanges =>
        isLoaded &&
        !string.Equals(SharedConfigurationText, loadedSharedConfigurationText, StringComparison.Ordinal);

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

    public async Task RefreshIfCleanAsync(CancellationToken cancellationToken = default)
    {
        if (HasSharedInstructionsChanges || HasSharedConfigurationChanges)
        {
            ConfigurationMessage = "Unsaved shared Codex edits were retained. Save or refresh explicitly to discard them.";
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefresh())
        {
            return;
        }

        IsBusy = true;
        ConfigurationMessage = "Loading shared Codex configuration...";
        try
        {
            var snapshot = await service
                .LoadAsync(activeWorkspacePath(), cancellationToken)
                .ConfigureAwait(true);
            ApplySnapshot(snapshot);
            ConfigurationMessage =
                $"Loaded {snapshot.Provenance.Count(source => source.Exists)} active configuration source(s).";
            reportStatus("Shared Codex configuration refreshed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ConfigurationMessage = ex.Message;
            logger.Log(
                AppLogLevel.Warning,
                "codex_configuration_refresh_failed",
                "Shared Codex configuration could not be refreshed.",
                exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SaveSharedInstructionsAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(CodexConfigurationFileKind.SharedInstructions, cancellationToken);

    public Task SaveSharedConfigurationAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(CodexConfigurationFileKind.SharedConfiguration, cancellationToken);

    public Task OpenSharedInstructionsAsync(CancellationToken cancellationToken = default) =>
        OpenSharedFileAsync(CodexConfigurationFileKind.SharedInstructions, cancellationToken);

    public Task OpenSharedConfigurationAsync(CancellationToken cancellationToken = default) =>
        OpenSharedFileAsync(CodexConfigurationFileKind.SharedConfiguration, cancellationToken);

    public void RaiseCommandStates() =>
        RaiseAllCommandStates();

    private void ApplySnapshot(CodexConfigurationSnapshot snapshot)
    {
        sharedInstructionsText = snapshot.SharedInstructions.Content;
        loadedSharedInstructionsText = snapshot.SharedInstructions.Content;
        sharedInstructionsRevision = snapshot.SharedInstructions.Revision;
        sharedInstructionsExists = snapshot.SharedInstructions.Exists;
        sharedConfigurationText = snapshot.SharedConfiguration.Content;
        loadedSharedConfigurationText = snapshot.SharedConfiguration.Content;
        sharedConfigurationRevision = snapshot.SharedConfiguration.Revision;
        sharedConfigurationExists = snapshot.SharedConfiguration.Exists;
        isLoaded = true;

        OnPropertyChanged(nameof(SharedInstructionsText));
        OnPropertyChanged(nameof(SharedConfigurationText));
        Provenance.Clear();
        foreach (var source in snapshot.Provenance)
        {
            Provenance.Add(source);
        }

        RaiseEditorState();
    }

    private async Task SaveAsync(
        CodexConfigurationFileKind kind,
        CancellationToken cancellationToken)
    {
        var text = kind == CodexConfigurationFileKind.SharedInstructions
            ? SharedInstructionsText
            : SharedConfigurationText;
        var validationMessage = Validate(text, kind);
        if (validationMessage is not null)
        {
            ConfigurationMessage = validationMessage;
            return;
        }

        IsBusy = true;
        try
        {
            var revision = kind == CodexConfigurationFileKind.SharedInstructions
                ? sharedInstructionsRevision
                : sharedConfigurationRevision;
            var saved = await service
                .SaveAsync(kind, text, revision, cancellationToken)
                .ConfigureAwait(true);
            ApplySavedDocument(saved);
            ConfigurationMessage =
                $"{Path.GetFileName(saved.Path)} saved. Codex applies shared changes to future task starts.";
            reportStatus($"{Path.GetFileName(saved.Path)} saved");
        }
        catch (CodexConfigurationConflictException)
        {
            ConfigurationMessage =
                $"{FileName(kind)} changed outside SynthiaCode. Refresh before saving so the external edit is not overwritten.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ConfigurationMessage = ex.Message;
            logger.Log(
                AppLogLevel.Warning,
                "codex_configuration_save_failed",
                $"The shared Codex {FileName(kind)} file could not be saved.",
                new Dictionary<string, string?> { ["kind"] = kind.ToString() },
                ex);
        }
        finally
        {
            IsBusy = false;
            RaiseEditorState();
        }
    }

    private void ApplySavedDocument(CodexConfigurationDocument saved)
    {
        if (saved.Kind == CodexConfigurationFileKind.SharedInstructions)
        {
            loadedSharedInstructionsText = saved.Content;
            sharedInstructionsRevision = saved.Revision;
            sharedInstructionsExists = saved.Exists;
        }
        else
        {
            loadedSharedConfigurationText = saved.Content;
            sharedConfigurationRevision = saved.Revision;
            sharedConfigurationExists = saved.Exists;
        }

        var index = Provenance
            .Select((source, sourceIndex) => (source, sourceIndex))
            .FirstOrDefault(pair => pair.source.Kind == saved.Kind)
            .sourceIndex;
        if (index >= 0 && index < Provenance.Count && Provenance[index].Kind == saved.Kind)
        {
            Provenance[index] = Provenance[index] with { Exists = true };
        }
    }

    private async Task OpenSharedFileAsync(
        CodexConfigurationFileKind kind,
        CancellationToken cancellationToken)
    {
        var hasChanges = kind == CodexConfigurationFileKind.SharedInstructions
            ? HasSharedInstructionsChanges
            : HasSharedConfigurationChanges;
        var exists = kind == CodexConfigurationFileKind.SharedInstructions
            ? sharedInstructionsExists
            : sharedConfigurationExists;
        if (!exists && hasChanges)
        {
            ConfigurationMessage = $"Save {FileName(kind)} before opening it in an external editor.";
            return;
        }

        try
        {
            var document = await service.EnsureExistsAsync(kind, cancellationToken).ConfigureAwait(true);
            ApplySavedDocument(document);
            openInEditor(document.Path);
            ConfigurationMessage = $"Opened {Path.GetFileName(document.Path)} in the configured editor.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ConfigurationMessage = ex.Message;
            logger.Log(
                AppLogLevel.Warning,
                "codex_configuration_open_failed",
                "A Codex configuration source could not be opened.",
                exception: ex);
        }
        finally
        {
            RaiseEditorState();
        }
    }

    private async Task OpenSourceAsync(object? parameter)
    {
        if (parameter is not CodexConfigurationSource source)
        {
            return;
        }

        if (source.Kind == CodexConfigurationFileKind.SharedInstructions)
        {
            await OpenSharedInstructionsAsync().ConfigureAwait(true);
            return;
        }

        if (source.Kind == CodexConfigurationFileKind.SharedConfiguration)
        {
            await OpenSharedConfigurationAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            openInEditor(source.Path);
            ConfigurationMessage = $"Opened {source.FileName} from {source.Scope}.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ConfigurationMessage = ex.Message;
        }
    }

    private void RevealSource(object? parameter)
    {
        if (parameter is not CodexConfigurationSource source)
        {
            return;
        }

        try
        {
            revealInExplorer(source.Exists ? source.Path : service.CodexHomePath);
            ConfigurationMessage = source.Exists
                ? $"Revealed {source.FileName} in Explorer."
                : "Opened the shared Codex configuration folder.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ConfigurationMessage = ex.Message;
        }
    }

    private bool CanRefresh() => !isShuttingDown() && !IsBusy;

    private bool CanSaveSharedInstructions() =>
        CanOpen() &&
        isLoaded &&
        HasSharedInstructionsChanges &&
        Validate(SharedInstructionsText, CodexConfigurationFileKind.SharedInstructions) is null;

    private bool CanSaveSharedConfiguration() =>
        CanOpen() &&
        isLoaded &&
        HasSharedConfigurationChanges &&
        Validate(SharedConfigurationText, CodexConfigurationFileKind.SharedConfiguration) is null;

    private bool CanOpen() => !isShuttingDown() && !IsBusy;

    private bool CanOpenSource(object? parameter) =>
        CanOpen() &&
        parameter is CodexConfigurationSource source &&
        (source.Exists || source.IsEditable);

    private bool CanRevealSource(object? parameter) =>
        !isShuttingDown() &&
        parameter is CodexConfigurationSource source &&
        (source.Exists || Directory.Exists(service.CodexHomePath));

    private static string? Validate(string text, CodexConfigurationFileKind kind) =>
        Encoding.UTF8.GetByteCount(text) <= 512 * 1024
            ? null
            : $"{FileName(kind)} must be 512 KiB or smaller.";

    private static string FileName(CodexConfigurationFileKind kind) => kind switch
    {
        CodexConfigurationFileKind.SharedInstructions => "AGENTS.md",
        CodexConfigurationFileKind.SharedConfiguration => "config.toml",
        _ => "configuration file"
    };

    private void RaiseEditorState()
    {
        OnPropertyChanged(nameof(HasSharedInstructionsChanges));
        OnPropertyChanged(nameof(HasSharedConfigurationChanges));
        RaiseAllCommandStates();
    }

    private void RaiseAllCommandStates()
    {
        refreshCommand.RaiseCanExecuteChanged();
        saveSharedInstructionsCommand.RaiseCanExecuteChanged();
        saveSharedConfigurationCommand.RaiseCanExecuteChanged();
        openSharedInstructionsCommand.RaiseCanExecuteChanged();
        openSharedConfigurationCommand.RaiseCanExecuteChanged();
        openSourceCommand.RaiseCanExecuteChanged();
        revealSourceCommand.RaiseCanExecuteChanged();
    }
}
