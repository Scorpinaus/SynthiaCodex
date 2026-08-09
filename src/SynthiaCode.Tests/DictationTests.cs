using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using System.Xml.Linq;

[Trait("Category", TestCategories.Wpf)]
[Collection(TestCategories.WpfCollection)]
public sealed class DictationTests
{


    [Fact(DisplayName = "dictation toggles and appends finalized speech to the prompt")]
    public async Task DictationTogglesAndAppendsToPromptAsync()
    {
        var speech = new FakeSpeechRecognitionService();
        var viewModel = CreateViewModel(speech);
        viewModel.Prompt = "Review this";

        await ((AsyncRelayCommand)viewModel.ToggleDictationCommand).ExecuteAsync();

        Assert(speech.StartCount == 1, "microphone starts once");
        Assert(viewModel.IsDictating, "view model enters listening state");
        Assert(viewModel.DictationStatusText == "Listening...", "listening state is announced");

        speech.EmitRecognized("and add tests");
        speech.EmitRecognized("   ");

        Assert(viewModel.Prompt == "Review this and add tests", "recognized speech is appended without replacing typed text");

        await ((AsyncRelayCommand)viewModel.ToggleDictationCommand).ExecuteAsync();

        Assert(speech.StopCount == 1, "second click stops the microphone");
        Assert(!viewModel.IsDictating, "view model exits listening state");
        Assert(viewModel.DictationStatusText == "Dictation stopped", "stopped state is announced");
    }

    [Fact(DisplayName = "dictation targets active task guidance")]
    public async Task DictationTargetsActiveGuidanceAsync()
    {
        var speech = new FakeSpeechRecognitionService();
        var viewModel = CreateViewModel(speech);
        viewModel.Prompt = "Unsent prompt";
        viewModel.SteeringText = "Check logs";
        viewModel.IsTurnRunning = true;

        await ((AsyncRelayCommand)viewModel.ToggleDictationCommand).ExecuteAsync();
        speech.EmitRecognized("then rerun tests");

        Assert(viewModel.SteeringText == "Check logs then rerun tests", "speech appends to active guidance");
        Assert(viewModel.Prompt == "Unsent prompt", "inactive prompt remains unchanged");
    }

    [Fact(DisplayName = "dictation surfaces microphone startup failures")]
    public async Task DictationSurfacesStartupFailureAsync()
    {
        var speech = new FakeSpeechRecognitionService
        {
            StartException = new InvalidOperationException("No microphone input device is available.")
        };
        var viewModel = CreateViewModel(speech);

        await ((AsyncRelayCommand)viewModel.ToggleDictationCommand).ExecuteAsync();

        Assert(!viewModel.IsDictating, "failed startup does not leave listening state active");
        Assert(
            viewModel.DictationStatusText == "Dictation unavailable: No microphone input device is available.",
            "startup failure is actionable");
    }

    [Fact(DisplayName = "dictation reports unavailable Windows speech recognition")]
    public Task DictationReportsUnavailableRecognitionAsync()
    {
        var speech = new FakeSpeechRecognitionService(
            new SpeechRecognitionAvailability(false, "Install a Windows speech recognizer to use dictation."));
        var viewModel = CreateViewModel(speech);

        Assert(!viewModel.IsDictationAvailable, "unavailable recognizer disables dictation");
        Assert(!viewModel.ToggleDictationCommand.CanExecute(null), "microphone command is disabled");
        Assert(
            viewModel.DictationToolTip == "Install a Windows speech recognizer to use dictation.",
            "unavailable reason is exposed");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "dictation stops and disposes with the task workspace")]
    public async Task DictationStopsAndDisposesAsync()
    {
        var speech = new FakeSpeechRecognitionService();
        var viewModel = CreateViewModel(speech);
        await ((AsyncRelayCommand)viewModel.ToggleDictationCommand).ExecuteAsync();

        await viewModel.DisposeAsync();

        Assert(speech.StopCount == 1, "workspace disposal stops active recognition");
        Assert(speech.DisposeCount == 1, "workspace disposal releases recognition resources");
    }

    [Fact(DisplayName = "composer renders an accessible microphone control")]
    public Task ComposerRendersMicrophoneControlAsync()
    {
        var root = FindRepositoryRoot();
        var taskView = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Views", "TaskComposerView.xaml"));
        var icons = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Themes", "Icons.xaml"));
        var document = XDocument.Parse(taskView);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictationButton = document
            .Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "DictationButton");

        Assert(taskView.Contains("x:Name=\"DictationButton\"", StringComparison.Ordinal), "composer names the microphone button");
        Assert(taskView.Contains("ToggleDictationCommand", StringComparison.Ordinal), "microphone button invokes dictation");
        Assert(taskView.Contains("IconMicrophone", StringComparison.Ordinal), "microphone button uses the vector icon");
        Assert(taskView.Contains("DictationStatusText", StringComparison.Ordinal), "composer exposes live dictation status");
        Assert(taskView.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "dictation status is announced accessibly");
        Assert(
            (string?)dictationButton.Attributes().SingleOrDefault(attribute =>
                attribute.Name.LocalName == "ToolTipService.ShowOnDisabled") == "True",
            "unavailable dictation explains itself while disabled");
        Assert(icons.Contains("x:Key=\"IconMicrophone\"", StringComparison.Ordinal), "theme resources contain a DPI-safe microphone icon");
        return Task.CompletedTask;
    }

    private static TaskViewModel CreateViewModel(ISpeechRecognitionService speech)
    {
        var actions = new TaskConversationActionStub();
        return new TaskViewModel(actions, actions, actions, actions, actions, speech);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SynthiaCode.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeSpeechRecognitionService : ISpeechRecognitionService
    {
        public FakeSpeechRecognitionService(SpeechRecognitionAvailability? availability = null)
        {
            Availability = availability ?? SpeechRecognitionAvailability.Available;
        }

        public event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized;

        public event EventHandler<SpeechRecognitionStoppedEventArgs>? Stopped;

        public SpeechRecognitionAvailability Availability { get; }

        public Exception? StartException { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return StartException is null ? Task.CompletedTask : Task.FromException(StartException);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            Stopped?.Invoke(this, new SpeechRecognitionStoppedEventArgs(null));
            return Task.CompletedTask;
        }

        public void EmitRecognized(string text) => SpeechRecognized?.Invoke(this, new SpeechRecognizedEventArgs(text));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
