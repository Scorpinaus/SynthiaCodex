using System.Text.Json;
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

    public JsonSettingsStore(string appDataDirectory, IAppLogger logger)
    {
        Directory.CreateDirectory(appDataDirectory);
        SettingsPath = Path.Combine(appDataDirectory, "settings.json");
        this.logger = logger;
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "settings_load_failed", "Settings could not be loaded; defaults will be used.", exception: ex);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tempPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, SettingsPath, overwrite: true);
    }
}
