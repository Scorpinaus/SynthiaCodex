using Microsoft.Win32.SafeHandles;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Terminal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NativeCodexAssistant.Infrastructure.Terminal;

public sealed class WindowsConPtyTerminalService(IAppLogger logger) : ITerminalService
{
    public Task<ITerminalSession> StartSessionAsync(
        TerminalStartRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The integrated terminal requires Windows ConPTY.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Terminal working directory does not exist: {request.WorkingDirectory}");
        }

        if (request.Columns is < 1 or > short.MaxValue || request.Rows is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Terminal dimensions must be between 1 and 32767.");
        }

        var session = ConPtyTerminalSession.Start(
            Path.GetFullPath(request.WorkingDirectory),
            checked((short)request.Columns),
            checked((short)request.Rows),
            logger);
        return Task.FromResult<ITerminalSession>(session);
    }
}

internal sealed class ConPtyTerminalSession : ITerminalSession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xffffffff;
    private static readonly Regex ControlSequencePattern = new(
        "(?:\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\))|(?:\\x1B(?:\\[[0-?]*[ -/]*[@-~]|[()][0-2A-Z]|[@-_]))|[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IAppLogger logger;
    private readonly FileStream inputStream;
    private readonly FileStream outputStream;
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly SemaphoreSlim disposeGate = new(1, 1);
    private readonly CancellationTokenSource readCancellation = new();
    private readonly IntPtr processHandle;
    private IntPtr pseudoConsole;
    private Task readTask = Task.CompletedTask;
    private Task waitTask = Task.CompletedTask;
    private int isRunning = 1;
    private bool isDisposed;

    private ConPtyTerminalSession(
        string workingDirectory,
        IntPtr pseudoConsole,
        IntPtr processHandle,
        FileStream inputStream,
        FileStream outputStream,
        IAppLogger logger)
    {
        Id = Guid.NewGuid().ToString("N");
        WorkingDirectory = workingDirectory;
        this.pseudoConsole = pseudoConsole;
        this.processHandle = processHandle;
        this.inputStream = inputStream;
        this.outputStream = outputStream;
        this.logger = logger;
    }

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public event EventHandler<TerminalExitedEventArgs>? Exited;

    public string Id { get; }

    public string WorkingDirectory { get; }

    public bool IsRunning => Volatile.Read(ref isRunning) == 1;

    public static ConPtyTerminalSession Start(string workingDirectory, short columns, short rows, IAppLogger logger)
    {
        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        FileStream? inputStream = null;
        FileStream? outputStream = null;

        try
        {
            ThrowLastWin32ErrorIfFalse(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            ThrowLastWin32ErrorIfFalse(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));

            var result = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out pseudoConsole);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            inputStream = new FileStream(
                new SafeFileHandle(inputWrite, ownsHandle: true),
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false);
            inputWrite = IntPtr.Zero;
            outputStream = new FileStream(
                new SafeFileHandle(outputRead, ownsHandle: true),
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
            outputRead = IntPtr.Zero;

            nuint attributeListSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
            ThrowLastWin32ErrorIfFalse(InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize));
            ThrowLastWin32ErrorIfFalse(UpdateProcThreadAttribute(
                attributeList,
                0,
                ProcThreadAttributePseudoConsole,
                pseudoConsole,
                (nuint)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero));

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles
                },
                AttributeList = attributeList
            };
            var commandLine = new StringBuilder("powershell.exe -NoLogo -NoProfile");
            ThrowLastWin32ErrorIfFalse(CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo));
            processHandle = processInfo.Process;
            CloseHandle(processInfo.Thread);
            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            var session = new ConPtyTerminalSession(
                workingDirectory,
                pseudoConsole,
                processHandle,
                inputStream,
                outputStream,
                logger);
            pseudoConsole = IntPtr.Zero;
            processHandle = IntPtr.Zero;
            inputStream = null;
            outputStream = null;
            session.StartBackgroundTasks();
            logger.Log(
                AppLogLevel.Information,
                "terminal_started",
                "A ConPTY PowerShell terminal was started.",
                new Dictionary<string, string?> { ["workingDirectory"] = workingDirectory, ["sessionId"] = session.Id });
            return session;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            inputStream?.Dispose();
            outputStream?.Dispose();
            CloseIfValid(inputRead);
            CloseIfValid(inputWrite);
            CloseIfValid(outputRead);
            CloseIfValid(outputWrite);
            CloseIfValid(processHandle);
            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    public async Task WriteInputAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (!IsRunning)
        {
            throw new InvalidOperationException("The terminal session has exited.");
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await inputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inputGate.Release();
        }
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (columns is < 1 or > short.MaxValue || rows is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be between 1 and 32767.");
        }

        var result = ResizePseudoConsole(pseudoConsole, new Coord(checked((short)columns), checked((short)rows)));
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            await WriteInputAsync("exit\r", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            logger.Log(AppLogLevel.Debug, "terminal_exit_write_failed", "The terminal did not accept a graceful exit command.", exception: ex);
        }

        var waitResult = await Task.Run(() => WaitForSingleObject(processHandle, 1000), cancellationToken).ConfigureAwait(false);
        if (waitResult == WaitTimeout)
        {
            ThrowLastWin32ErrorIfFalse(TerminateProcess(processHandle, 1));
            await Task.Run(() => WaitForSingleObject(processHandle, 5000), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isDisposed)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Log(AppLogLevel.Warning, "terminal_stop_failed", "The terminal session did not stop cleanly.", exception: ex);
                if (IsRunning)
                {
                    TerminateProcess(processHandle, 1);
                }
            }

            isDisposed = true;
            readCancellation.Cancel();
            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
            }

            inputStream.Dispose();
            outputStream.Dispose();
            try
            {
                await Task.WhenAll(readTask, waitTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
            {
                logger.Log(AppLogLevel.Debug, "terminal_background_stop", "Terminal background tasks ended during disposal.", exception: ex);
            }

            CloseHandle(processHandle);
            readCancellation.Dispose();
            inputGate.Dispose();
            logger.Log(AppLogLevel.Information, "terminal_disposed", "A terminal session was disposed.", new Dictionary<string, string?> { ["sessionId"] = Id });
        }
        finally
        {
            disposeGate.Release();
        }
    }

    private void StartBackgroundTasks()
    {
        readTask = Task.Run(() => ReadOutputAsync(readCancellation.Token));
        waitTask = WaitForExitAsync();
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(outputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[2048];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var readable = ControlSequencePattern.Replace(new string(buffer, 0, count), string.Empty);
                if (!string.IsNullOrEmpty(readable))
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs(readable));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (!isDisposed)
            {
                logger.Log(AppLogLevel.Debug, "terminal_output_ended", "Terminal output streaming ended.", exception: ex);
            }
        }
    }

    private async Task WaitForExitAsync()
    {
        await Task.Run(() => WaitForSingleObject(processHandle, Infinite)).ConfigureAwait(false);
        if (Interlocked.Exchange(ref isRunning, 0) == 0)
        {
            return;
        }

        GetExitCodeProcess(processHandle, out var exitCode);
        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
        }

        try
        {
            await readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            logger.Log(AppLogLevel.Debug, "terminal_output_drain_failed", "Terminal output did not fully drain after process exit.", exception: ex);
        }

        Exited?.Invoke(this, new TerminalExitedEventArgs(unchecked((int)exitCode)));
        logger.Log(
            AppLogLevel.Information,
            "terminal_exited",
            "A terminal process exited.",
            new Dictionary<string, string?> { ["sessionId"] = Id, ["exitCode"] = exitCode.ToString() });
    }

    private static void ThrowLastWin32ErrorIfFalse(bool result)
    {
        if (!result)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr pipeAttributes, uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
