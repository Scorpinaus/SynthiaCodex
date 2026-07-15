using System.Text.Json;
using System.Diagnostics;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Settings;

namespace NativeCodexAssistant.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
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
        this.logger = logger;
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
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

        return new AppSettings();
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
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
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
