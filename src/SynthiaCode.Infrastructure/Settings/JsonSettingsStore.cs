using System.Text.Json;
using System.Diagnostics;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Settings;

public sealed class JsonSettingsStore : ILegacySettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IAppLogger logger;
    private readonly SemaphoreSlim saveGate = new(1, 1);

    public JsonSettingsStore(string appDataDirectory, IAppLogger logger)
    {
        Directory.CreateDirectory(appDataDirectory);
        SettingsPath = Path.Combine(appDataDirectory, "settings.json");
        BackupPath = Path.Combine(appDataDirectory, "settings.release-0.1.0.backup.json");
        this.logger = logger;
    }

    public string SettingsPath { get; }

    public string BackupPath { get; }

    public bool Exists => File.Exists(SettingsPath) || File.Exists(SettingsPath + ".tmp");

    public bool BackupExists => File.Exists(BackupPath);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await TryLoadAuthoritativeAsync(cancellationToken).ConfigureAwait(false);
        if (settings is not null)
        {
            return settings;
        }

        settings = new AppSettings();
        AppSettingsHarnessMigration.Apply(settings);
        return settings;
    }

    private async Task<AppSettings?> TryLoadAuthoritativeAsync(CancellationToken cancellationToken)
    {
        var tempPath = SettingsPath + ".tmp";
        var temporaryAttempted = false;
        if (File.Exists(tempPath) &&
            (!File.Exists(SettingsPath) || File.GetLastWriteTimeUtc(tempPath) >= File.GetLastWriteTimeUtc(SettingsPath)))
        {
            temporaryAttempted = true;
            var interruptedSave = await TryLoadAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (interruptedSave is not null)
            {
                PromoteTemporaryFile(tempPath);
                return interruptedSave;
            }
        }

        if (File.Exists(SettingsPath))
        {
            var primary = await TryLoadAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
            if (primary is not null)
            {
                TryDeleteStaleTemporaryFile(tempPath);
                return primary;
            }
        }

        if (!temporaryAttempted && File.Exists(tempPath))
        {
            var recovered = await TryLoadAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                PromoteTemporaryFile(tempPath);
                return recovered;
            }
        }

        return null;
    }

    private void PromoteTemporaryFile(string tempPath)
    {
        File.Move(tempPath, SettingsPath, overwrite: true);
        logger.Log(
            AppLogLevel.Warning,
            "settings_recovered_from_temporary_file",
            "Settings were recovered from an interrupted atomic-save temporary file.");
    }

    private async Task<AppSettings?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (settings is not null)
            {
                AppSettingsHarnessMigration.Apply(settings);
            }
            return settings;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "settings_load_failed",
                $"Settings could not be loaded from {Path.GetFileName(path)}.",
                exception: ex);
            return null;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettingsHarnessMigration.Apply(settings);

        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timer = Stopwatch.StartNew();
            var tempPath = SettingsPath + ".tmp";
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, SettingsPath, overwrite: true);
            logger.Log(
                AppLogLevel.Information,
                "settings_saved",
                "Application settings were saved atomically.",
                new Dictionary<string, string?>
                {
                    ["elapsedMilliseconds"] = timer.ElapsedMilliseconds.ToString(),
                    ["serializedBytes"] = new FileInfo(SettingsPath).Length.ToString()
                });
        }
        finally
        {
            saveGate.Release();
        }
    }

    public async Task CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(BackupPath))
            {
                return;
            }

            var source = await TryLoadAuthoritativeAsync(cancellationToken).ConfigureAwait(false);
            if (source is null || !File.Exists(SettingsPath))
            {
                throw new InvalidDataException(
                    "The release 0.1.0 settings document is missing or invalid and cannot be backed up.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(SettingsPath, BackupPath, overwrite: false);
            logger.Log(
                AppLogLevel.Information,
                "legacy_settings_backup_created",
                "The release 0.1.0 settings document was backed up before import.");
        }
        finally
        {
            saveGate.Release();
        }
    }

    public async Task<AppSettings> LoadBackupAsync(CancellationToken cancellationToken = default)
    {
        var settings = await TryLoadAsync(BackupPath, cancellationToken).ConfigureAwait(false);
        return settings ?? throw new InvalidDataException(
            "The release 0.1.0 settings backup is missing or invalid.");
    }

    private void TryDeleteStaleTemporaryFile(string tempPath)
    {
        if (!File.Exists(tempPath))
        {
            return;
        }

        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "settings_temporary_cleanup_failed",
                "A stale settings temporary file could not be removed.",
                exception: ex);
        }
    }
}
