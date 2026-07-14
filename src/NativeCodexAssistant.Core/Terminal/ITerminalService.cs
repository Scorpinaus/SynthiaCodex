namespace NativeCodexAssistant.Core.Terminal;

public sealed record TerminalStartRequest(string WorkingDirectory, int Columns = 120, int Rows = 30);

public sealed class TerminalOutputEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public sealed class TerminalExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}

public interface ITerminalService
{
    Task<ITerminalSession> StartSessionAsync(
        TerminalStartRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITerminalSession : IAsyncDisposable
{
    event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    event EventHandler<TerminalExitedEventArgs>? Exited;

    string Id { get; }

    string WorkingDirectory { get; }

    bool IsRunning { get; }

    Task WriteInputAsync(string text, CancellationToken cancellationToken = default);

    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
