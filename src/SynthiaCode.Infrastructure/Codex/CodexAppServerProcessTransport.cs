using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Codex;

public sealed class CodexAppServerProcessTransport(
    string executablePath,
    IAppLogger logger,
    CodexRuntimeEnvironment? runtimeEnvironment = null) : IAppServerTransport
{
    private static readonly Encoding ProtocolEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private Process? process;
    private Task? stderrTask;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (process is not null)
        {
            return Task.CompletedTask;
        }

        process = new Process
        {
            StartInfo = CreateStartInfo(executablePath)
        };
        runtimeEnvironment?.ApplyTo(process.StartInfo);

        process.Start();
        stderrTask = Task.Run(ReadStandardErrorAsync, CancellationToken.None);
        logger.Log(AppLogLevel.Information, "codex_app_server_started", "Started codex app-server over stdio.");

        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentProcess = EnsureProcess();
        if (currentProcess.HasExited)
        {
            throw new InvalidOperationException("Cannot write to codex app-server because the process has exited.");
        }

        await currentProcess.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await currentProcess.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentProcess = EnsureProcess();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await currentProcess.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var currentProcess = process;
        if (currentProcess is null)
        {
            return;
        }

        try
        {
            if (!currentProcess.HasExited)
            {
                currentProcess.Kill(entireProcessTree: true);
            }

            await currentProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (DecoderFallbackException ex)
        {
            logger.Log(
                AppLogLevel.Error,
                "codex_app_server_stderr_invalid_utf8",
                "Codex app-server stderr contained invalid UTF-8.",
                exception: ex);
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "codex_app_server_stop_failed", "Stopping codex app-server failed.", exception: ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (stderrTask is not null)
        {
            await stderrTask.ConfigureAwait(false);
        }

        process?.Dispose();
    }

    private async Task ReadStandardErrorAsync()
    {
        var currentProcess = process;
        if (currentProcess is null)
        {
            return;
        }

        try
        {
            while (true)
            {
                var line = await currentProcess.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.Log(AppLogLevel.Warning, "codex_app_server_stderr", line);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private Process EnsureProcess()
    {
        return process ?? throw new InvalidOperationException("codex app-server process has not started.");
    }

    private static ProcessStartInfo CreateStartInfo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = ProtocolEncoding,
            StandardInputEncoding = ProtocolEncoding,
            StandardOutputEncoding = ProtocolEncoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var extension = Path.GetExtension(path);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.FileName = path;
        }

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        return startInfo;
    }
}
