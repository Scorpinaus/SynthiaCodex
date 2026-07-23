using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Codex;

public static class CodexDiagnosticStoreMaintenance
{
    public const long DefaultMaximumBytes = 32L * 1024 * 1024;

    public static CodexDiagnosticStoreCleanupResult TrimOversizedStore(
        string codexHomePath,
        IAppLogger logger,
        long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        var normalizedHomePath = Path.GetFullPath(codexHomePath);
        if (!Directory.Exists(normalizedHomePath))
        {
            return CodexDiagnosticStoreCleanupResult.Empty;
        }

        var candidates = new List<DiagnosticFile>();
        var failedFileCount = 0;
        foreach (var path in Directory.EnumerateFiles(normalizedHomePath, "logs_*.sqlite*", SearchOption.TopDirectoryOnly))
        {
            if (!IsCodexLogDatabaseFile(Path.GetFileName(path)))
            {
                continue;
            }

            try
            {
                candidates.Add(new DiagnosticFile(path, new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failedFileCount++;
                LogFailure(logger, "inspect", path, ex);
            }
        }

        var observedBytes = candidates.Sum(file => file.Length);
        if (observedBytes <= maximumBytes)
        {
            return new CodexDiagnosticStoreCleanupResult(
                observedBytes,
                RemovedBytes: 0,
                RemovedFileCount: 0,
                failedFileCount);
        }

        long removedBytes = 0;
        var removedFileCount = 0;
        foreach (var file in candidates.OrderBy(file => DeletionOrder(file.Path)))
        {
            try
            {
                File.Delete(file.Path);
                removedBytes += file.Length;
                removedFileCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failedFileCount++;
                LogFailure(logger, "remove", file.Path, ex);
            }
        }

        logger.Log(
            AppLogLevel.Information,
            "codex_diagnostic_store_trimmed",
            "Oversized Codex SQLite diagnostics were removed before starting Codex.",
            new Dictionary<string, string?>
            {
                ["observedBytes"] = observedBytes.ToString(),
                ["maximumBytes"] = maximumBytes.ToString(),
                ["removedBytes"] = removedBytes.ToString(),
                ["removedFileCount"] = removedFileCount.ToString(),
                ["failedFileCount"] = failedFileCount.ToString()
            });

        return new CodexDiagnosticStoreCleanupResult(
            observedBytes,
            removedBytes,
            removedFileCount,
            failedFileCount);
    }

    private static bool IsCodexLogDatabaseFile(string fileName)
    {
        const string prefix = "logs_";
        var sqliteMarker = fileName.IndexOf(".sqlite", StringComparison.OrdinalIgnoreCase);
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || sqliteMarker <= prefix.Length)
        {
            return false;
        }

        var version = fileName.AsSpan(prefix.Length, sqliteMarker - prefix.Length);
        if (!version.ToString().All(char.IsAsciiDigit))
        {
            return false;
        }

        var suffix = fileName[sqliteMarker..];
        return suffix.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals(".sqlite-wal", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals(".sqlite-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static int DeletionOrder(string path) =>
        Path.GetFileName(path).EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static void LogFailure(IAppLogger logger, string operation, string path, Exception exception)
    {
        logger.Log(
            AppLogLevel.Warning,
            "codex_diagnostic_store_maintenance_failed",
            $"Could not {operation} a Codex SQLite diagnostic file.",
            new Dictionary<string, string?> { ["path"] = path },
            exception);
    }

    private sealed record DiagnosticFile(string Path, long Length);
}

public sealed record CodexDiagnosticStoreCleanupResult(
    long ObservedBytes,
    long RemovedBytes,
    int RemovedFileCount,
    int FailedFileCount)
{
    public static CodexDiagnosticStoreCleanupResult Empty { get; } = new(0, 0, 0, 0);
}
