using System.Diagnostics;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;

namespace NativeCodexAssistant.Infrastructure.Codex;

public sealed class CodexDiscoveryService(IAppLogger logger) : ICodexDiscoveryService
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
        var executable = ResolveExecutable(preferredExecutablePath);
        if (executable is null)
        {
            return CodexInstallation.Missing("Install Codex CLI or set a preferred executable path in settings.");
        }

        var version = await TryReadVersionAsync(executable, cancellationToken).ConfigureAwait(false);
        return new CodexInstallation(
            true,
            executable,
            version,
            version is null ? "Codex CLI found" : $"Codex CLI {version}",
            "Codex executable was resolved from the configured path or PATH.");
    }

    private static string? ResolveExecutable(string? preferredExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(preferredExecutablePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(preferredExecutablePath);
            if (File.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in WindowsExecutableNames)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task<string?> TryReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = CreateProcess(executablePath, "--version");
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(4), cancellationToken))
                .ConfigureAwait(false);

            if (!ReferenceEquals(completed, waitTask) && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
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
