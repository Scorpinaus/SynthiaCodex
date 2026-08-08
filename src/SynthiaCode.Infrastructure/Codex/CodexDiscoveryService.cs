using System.Diagnostics;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Codex;

public sealed class CodexDiscoveryService(
    IAppLogger logger,
    CodexRuntimeEnvironment? runtimeEnvironment = null) : ICodexDiscoveryService
{
    private static readonly string[] WindowsExecutableNames =
    [
        "codex.exe",
        "codex.cmd",
        "codex.bat",
        "codex"
    ];

    public async Task<CodexInstallation> DetectAsync(
        string? preferredExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(preferredExecutablePath))
        {
            var expandedPreferredPath = Environment.ExpandEnvironmentVariables(preferredExecutablePath);
            if (File.Exists(expandedPreferredPath))
            {
                var preferredVersion = await TryReadVersionAsync(
                        Path.GetFullPath(expandedPreferredPath),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(preferredVersion))
                {
                    return CreateInstallation(Path.GetFullPath(expandedPreferredPath), preferredVersion);
                }
            }
        }

        var sawCandidate = false;
        CodexInstallation? firstRunnable = null;
        CodexInstallation? newestRunnable = null;
        Version? newestVersion = null;
        foreach (var executable in EnumerateExecutableCandidates(preferredExecutablePath: null))
        {
            sawCandidate = true;
            var version = await TryReadVersionAsync(executable, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(version))
            {
                var installation = CreateInstallation(executable, version);
                firstRunnable ??= installation;
                if (TryParseComparableVersion(version, out var parsedVersion) &&
                    (newestVersion is null || parsedVersion > newestVersion))
                {
                    newestVersion = parsedVersion;
                    newestRunnable = installation;
                }
            }
        }

        if (newestRunnable is not null)
        {
            return newestRunnable;
        }

        if (firstRunnable is not null)
        {
            return firstRunnable;
        }

        return CodexInstallation.Missing(
            sawCandidate
                ? "Codex executable candidates were found, but none could be run with `--version`."
                : "Install Codex CLI or set a preferred executable path in settings.");
    }

    private static CodexInstallation CreateInstallation(string executable, string version) =>
        new(
            true,
            executable,
            version,
            $"Codex CLI {version}",
            "Codex executable was resolved from the configured path or selected as the newest runnable standalone, PATH, or OpenAI Codex app installation.");

    private static bool TryParseComparableVersion(string versionText, out Version version)
    {
        const string prefix = "codex-cli ";
        var value = versionText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? versionText[prefix.Length..]
            : versionText;
        var prereleaseSeparator = value.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseSeparator >= 0)
        {
            value = value[..prereleaseSeparator];
        }

        return Version.TryParse(value, out version!);
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(string? preferredExecutablePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preferredExecutablePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(preferredExecutablePath);
            if (File.Exists(expanded))
            {
                foreach (var candidate in YieldIfNew(Path.GetFullPath(expanded), seen))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var directory in EnumerateStandaloneCodexBinDirectories())
        {
            foreach (var candidate in EnumerateDirectoryCandidates(directory, seen))
            {
                yield return candidate;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var candidate in EnumerateDirectoryCandidates(directory, seen))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var directory in EnumerateKnownCodexBinDirectories())
        {
            foreach (var candidate in EnumerateDirectoryCandidates(directory, seen))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateStandaloneCodexBinDirectories()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(
                Environment.ExpandEnvironmentVariables(localAppData),
                "Programs",
                "OpenAI",
                "Codex",
                "bin");
        }
    }

    private static IEnumerable<string> EnumerateDirectoryCandidates(string directory, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var name in WindowsExecutableNames)
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            foreach (var uniqueCandidate in YieldIfNew(Path.GetFullPath(candidate), seen))
            {
                yield return uniqueCandidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateKnownCodexBinDirectories()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(Environment.ExpandEnvironmentVariables(appData), "npm");
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(Environment.ExpandEnvironmentVariables(localAppData), "OpenAI", "Codex", "bin");
        }
    }

    private static IEnumerable<string> YieldIfNew(string candidate, HashSet<string> seen)
    {
        if (seen.Add(candidate))
        {
            yield return candidate;
        }
    }

    private async Task<string?> TryReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = CreateProcess(executablePath, "--version");
            runtimeEnvironment?.ApplyTo(process.StartInfo);
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(4), cancellationToken))
                .ConfigureAwait(false);

            if (!ReferenceEquals(completed, waitTask) && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output;
            }

            logger.Log(
                AppLogLevel.Warning,
                "codex_version_failed",
                "Codex was found but version detection did not return a clean result.",
                new Dictionary<string, string?>
                {
                    ["exitCode"] = process.ExitCode.ToString(),
                    ["stderr"] = error
                });
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "codex_version_exception", "Codex version detection failed.", exception: ex);
        }

        return null;
    }

    private static Process CreateProcess(string executablePath, string argument)
    {
        var extension = Path.GetExtension(executablePath);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var shellProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            shellProcess.StartInfo.ArgumentList.Add("/c");
            shellProcess.StartInfo.ArgumentList.Add(executablePath);
            shellProcess.StartInfo.ArgumentList.Add(argument);
            return shellProcess;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(argument);
        return process;
    }
}
