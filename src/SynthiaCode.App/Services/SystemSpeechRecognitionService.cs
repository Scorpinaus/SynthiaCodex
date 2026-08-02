using System.Globalization;
using System.Speech.Recognition;
using SynthiaCode.Core.Logging;
using SystemSpeechRecognizedEventArgs = System.Speech.Recognition.SpeechRecognizedEventArgs;

namespace SynthiaCode.App.Services;

public sealed class SystemSpeechRecognitionService : ISpeechRecognitionService
{
    private readonly object syncRoot = new();
    private readonly IAppLogger logger;
    private readonly RecognizerInfo? recognizerInfo;
    private SpeechRecognitionEngine? recognizer;
    private SynchronizationContext? callbackContext;
    private bool isDisposed;

    public SystemSpeechRecognitionService(IAppLogger logger)
    {
        this.logger = logger;
        (Availability, recognizerInfo) = ResolveRecognizer();
    }

    public event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized;

    public event EventHandler<SpeechRecognitionStoppedEventArgs>? Stopped;

    public SpeechRecognitionAvailability Availability { get; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (!Availability.IsAvailable || recognizerInfo is null)
            {
                throw new InvalidOperationException(Availability.Message);
            }

            if (recognizer is not null)
            {
                return Task.CompletedTask;
            }

            callbackContext = SynchronizationContext.Current;
            SpeechRecognitionEngine? created = null;
            try
            {
                created = new SpeechRecognitionEngine(recognizerInfo);
                created.LoadGrammar(new DictationGrammar());
                created.SpeechRecognized += OnSpeechRecognized;
                created.RecognizeCompleted += OnRecognizeCompleted;
                created.SetInputToDefaultAudioDevice();
                recognizer = created;
                created.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                if (ReferenceEquals(recognizer, created))
                {
                    recognizer = null;
                }
                DetachAndDispose(created);
                logger.Log(
                    AppLogLevel.Warning,
                    "dictation_start_failed",
                    "Windows dictation could not start.",
                    exception: ex);
                throw new InvalidOperationException(
                    $"Check Windows microphone privacy settings and the default input device. {ex.Message}",
                    ex);
            }
        }

        logger.Log(
            AppLogLevel.Information,
            "dictation_started",
            "Windows dictation started.",
            new Dictionary<string, string?>
            {
                ["culture"] = recognizerInfo.Culture.Name
            });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SpeechRecognitionEngine? active;
        lock (syncRoot)
        {
            active = recognizer;
            recognizer = null;
        }

        if (active is null)
        {
            return Task.CompletedTask;
        }

        DetachAndDispose(active);
        logger.Log(AppLogLevel.Information, "dictation_stopped", "Windows dictation stopped.");
        PostStopped(null);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SpeechRecognitionEngine? active;
        lock (syncRoot)
        {
            if (isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            isDisposed = true;
            active = recognizer;
            recognizer = null;
        }

        DetachAndDispose(active);
        return ValueTask.CompletedTask;
    }

    private void OnSpeechRecognized(object? sender, SystemSpeechRecognizedEventArgs args)
    {
        var text = args.Result?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Post(() => SpeechRecognized?.Invoke(this, new SpeechRecognizedEventArgs(text)));
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs args)
    {
        SpeechRecognitionEngine? completed = null;
        lock (syncRoot)
        {
            if (ReferenceEquals(recognizer, sender))
            {
                completed = recognizer;
                recognizer = null;
            }
        }

        if (completed is null)
        {
            return;
        }

        DetachAndDispose(completed);
        var message = args.Error is null
            ? null
            : $"Speech recognition stopped unexpectedly: {args.Error.Message}";
        if (message is not null)
        {
            logger.Log(
                AppLogLevel.Warning,
                "dictation_stopped_unexpectedly",
                "Windows dictation stopped unexpectedly.",
                exception: args.Error);
        }

        PostStopped(message);
    }

    private void PostStopped(string? errorMessage) =>
        Post(() => Stopped?.Invoke(this, new SpeechRecognitionStoppedEventArgs(errorMessage)));

    private void Post(Action action)
    {
        var context = callbackContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            action();
            return;
        }

        context.Post(static state => ((Action)state!).Invoke(), action);
    }

    private void DetachAndDispose(SpeechRecognitionEngine? engine)
    {
        if (engine is null)
        {
            return;
        }

        engine.SpeechRecognized -= OnSpeechRecognized;
        engine.RecognizeCompleted -= OnRecognizeCompleted;
        try
        {
            engine.RecognizeAsyncCancel();
        }
        catch (InvalidOperationException)
        {
        }
        engine.Dispose();
    }

    private static (SpeechRecognitionAvailability Availability, RecognizerInfo? Recognizer) ResolveRecognizer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (new(false, "Dictation is available only on Windows."), null);
        }

        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed.Count == 0)
            {
                return (new(false, "Install a Windows speech recognizer to use dictation."), null);
            }

            var current = CultureInfo.CurrentUICulture;
            var selected = installed.FirstOrDefault(item => item.Culture.Equals(current))
                ?? installed.FirstOrDefault(item => string.Equals(
                    item.Culture.TwoLetterISOLanguageName,
                    current.TwoLetterISOLanguageName,
                    StringComparison.OrdinalIgnoreCase))
                ?? installed[0];
            return (SpeechRecognitionAvailability.Available, selected);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return (new(false, $"Windows speech recognition is unavailable: {ex.Message}"), null);
        }
    }
}
