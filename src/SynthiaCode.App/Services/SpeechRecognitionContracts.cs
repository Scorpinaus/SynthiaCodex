namespace SynthiaCode.App.Services;

public sealed record SpeechRecognitionAvailability(bool IsAvailable, string Message)
{
    public static SpeechRecognitionAvailability Available { get; } =
        new(true, "Start dictation");
}

public sealed class SpeechRecognizedEventArgs(string text) : EventArgs
{
    public string Text { get; } = text ?? string.Empty;
}

public sealed class SpeechRecognitionStoppedEventArgs(string? errorMessage) : EventArgs
{
    public string? ErrorMessage { get; } = errorMessage;
}

public interface ISpeechRecognitionService : IAsyncDisposable
{
    event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized;

    event EventHandler<SpeechRecognitionStoppedEventArgs>? Stopped;

    SpeechRecognitionAvailability Availability { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class UnavailableSpeechRecognitionService : ISpeechRecognitionService
{
    private UnavailableSpeechRecognitionService()
    {
    }

    public static UnavailableSpeechRecognitionService Instance { get; } = new();

    public event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized
    {
        add { }
        remove { }
    }

    public event EventHandler<SpeechRecognitionStoppedEventArgs>? Stopped
    {
        add { }
        remove { }
    }

    public SpeechRecognitionAvailability Availability { get; } =
        new(false, "Windows speech recognition is unavailable.");

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(Availability.Message));

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
