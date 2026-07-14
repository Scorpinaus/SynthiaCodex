using System.Diagnostics;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;

namespace NativeCodexAssistant.Infrastructure.Codex;

public sealed class CodexCliUtilityRunner(IAppLogger logger) : ICodexCliUtilityRunner
{
    public Task<CodexCliUtilityResult> RunDoctorAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default) =>
        RunAsync(installation, "doctor", cancellationToken);

    private async Task<CodexCliUtilityResult> RunAsync(
        CodexInstallation installation,
        string command,
        CancellationToken cancellationToken)
    {
        if (!installation.IsFound || string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            throw new InvalidOperationException("Codex CLI is not available.");
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(installation.ExecutablePath, command)
        };

        logger.Log(
            AppLogLevel.Information,
            "codex_utility_started",
            $"Started codex {command}.",
            new Dictionary<string, string?> { ["command"] = command });

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var result = new CodexCliUtilityResult(
            command,
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));

        logger.Log(
            result.Succeeded ? AppLogLevel.Information : AppLogLevel.Warning,
            "codex_utility_completed",
            $"codex {command} exited with code {result.ExitCode}.",
            new Dictionary<string, string?>
            {
                ["command"] = command,
                ["exitCode"] = result.ExitCode.ToString()
            });

        return result;
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string command)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var extension = Path.GetExtension(executablePath);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(executablePath);
        }
        else
        {
            startInfo.FileName = executablePath;
        }

        startInfo.ArgumentList.Add(command);
        return startInfo;
    }
}
