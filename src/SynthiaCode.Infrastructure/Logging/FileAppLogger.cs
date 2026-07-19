using System.Text.Json;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object syncRoot = new();
    private readonly string logPath;

    public FileAppLogger(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        var logDirectory = Path.Combine(appDataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        logPath = Path.Combine(logDirectory, "synthiacode.log.jsonl");
    }

    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        Exception? exception = null)
    {
        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level = level.ToString(),
            eventName,
            message,
            properties,
            exception = exception is null
                ? null
                : new
                {
                    type = exception.GetType().FullName,
                    exception.Message,
                    exception.StackTrace
                }
        };

        var line = JsonSerializer.Serialize(entry);
        lock (syncRoot)
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
    }
}
