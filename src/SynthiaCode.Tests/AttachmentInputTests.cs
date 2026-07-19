using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex;

internal static class AttachmentInputTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("attachment protocol serializes ordered multimodal input", ProtocolSerializesOrderedInputAsync),
        ("model catalog advertises image input capability", ModelCatalogAdvertisesImageCapabilityAsync),
        ("managed attachment store copies validates and deduplicates images", StoreCopiesValidatesAndDeduplicatesAsync),
        ("attachment references survive queue and settings snapshots", ReferencesSurviveQueueAndSettingsSnapshotsAsync),
        ("composer attachment state validates model capability", ComposerAttachmentStateValidatesCapabilityAsync),
        ("task view exposes attachment picker previews and drop target", TaskViewExposesAttachmentSurfaceAsync)
    ];

    private static async Task ProtocolSerializesOrderedInputAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("attachment_tests", "Attachment Tests", "1.0"));
        await InitializeAsync(client, transport);

        var imagePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "attachment-tests", "shot.png"));
        var startTask = client.StartTurnAsync(new CodexTurnStartRequest(
            "thr_images",
            [new CodexTextInput("Inspect this UI"), new CodexLocalImageInput(imagePath)],
            @"C:\Repo",
            CodexSandbox.WorkspaceWrite));

        await transport.WaitForClientMessageCountAsync(3);
        var start = JsonNode.Parse(transport.ClientMessages[2])!.AsObject();
        Assert(ReadString(start, "method") == "turn/start", "start method");
        Assert(ReadString(start, "params.input.0.type") == "text", "text part type");
        Assert(ReadString(start, "params.input.0.text") == "Inspect this UI", "text part value");
        Assert(ReadString(start, "params.input.1.type") == "localImage", "image part type");
        Assert(ReadString(start, "params.input.1.path") == imagePath, "image part path");
        transport.ServerSend("""{"id":1,"result":{"turn":{"id":"turn_images"}}}""");
        Assert((await startTask).TurnId == "turn_images", "start result");

        var steerTask = client.SteerTurnAsync(new CodexTurnSteerRequest(
            "thr_images",
            "turn_images",
            [new CodexLocalImageInput(imagePath)]));
        await transport.WaitForClientMessageCountAsync(4);
        var steer = JsonNode.Parse(transport.ClientMessages[3])!.AsObject();
        Assert(ReadString(steer, "params.input.0.type") == "localImage", "steer image part type");
        Assert(ReadString(steer, "params.input.0.path") == imagePath, "steer image path");
        transport.ServerSend("""{"id":2,"result":{"turnId":"turn_images"}}""");
        Assert((await steerTask).TurnId == "turn_images", "steer result");
    }

    private static async Task ModelCatalogAdvertisesImageCapabilityAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("attachment_tests", "Attachment Tests", "1.0"));
        await InitializeAsync(client, transport);

        var listTask = client.ListModelsAsync();
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend(
            """
            {"id":1,"result":{"data":[
              {"id":"vision","model":"gpt-vision","displayName":"Vision","description":"Images","isDefault":true,"hidden":false,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[],"inputModalities":["text","image"]},
              {"id":"text","model":"gpt-text","displayName":"Text","description":"Text only","isDefault":false,"hidden":false,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[],"inputModalities":["text"]}
            ],"nextCursor":null}}
            """);

        var models = await listTask;
        Assert(models.Single(model => model.Model == "gpt-vision").SupportsImageInput, "vision model supports images");
        Assert(!models.Single(model => model.Model == "gpt-text").SupportsImageInput, "text model rejects images");
    }

    private static async Task StoreCopiesValidatesAndDeduplicatesAsync()
    {
        using var temp = TempWorkspace.Create();
        var source = Path.Combine(temp.Root, "source.png");
        await File.WriteAllBytesAsync(source, TinyPng);
        var store = new LocalAttachmentStore(Path.Combine(temp.Root, "managed"), new TestLogger());

        var first = await store.ImportFileAsync(source);
        var second = await store.ImportFileAsync(source);
        File.Delete(source);

        Assert(first.StorageKey == second.StorageKey, "identical bytes deduplicate");
        Assert(first.MediaType == "image/png", "PNG media type detected");
        Assert(first.PixelWidth == 1 && first.PixelHeight == 1, "PNG dimensions detected");
        Assert(File.Exists(store.ResolvePath(first)), "managed copy survives source deletion");
        Assert(!Path.IsPathRooted(first.StorageKey), "persisted storage key is relative");

        var invalid = Path.Combine(temp.Root, "invalid.png");
        await File.WriteAllTextAsync(invalid, "not an image");
        await AssertThrowsAsync<InvalidDataException>(() => store.ImportFileAsync(invalid), "invalid image rejected");
    }

    private static Task ReferencesSurviveQueueAndSettingsSnapshotsAsync()
    {
        var image = Reference("objects/aa/image.png");
        var queue = new CodexFollowUpQueue();
        var queued = queue.Enqueue("Inspect it", Options(@"D:\Repo"), [image]);
        var settings = new AppSettings
        {
            ProjectThreads =
            [
                new PersistedProjectThread
                {
                    ProjectPath = @"D:\Repo",
                    ThreadId = "thr_image",
                    QueuedFollowUps = [queued.Snapshot()],
                    ConversationTurns =
                    [
                        new CodexConversationTurnSnapshot
                        {
                            TurnId = "turn_image",
                            UserPrompt = "Inspect it",
                            UserImages = [image]
                        }
                    ]
                }
            ],
            ComposerAttachmentDrafts =
            [
                new ComposerAttachmentDraftSnapshot
                {
                    ProjectPath = @"D:\Repo",
                    ThreadId = "thr_image",
                    Images = [image]
                }
            ]
        };

        var snapshot = AppSettingsSnapshot.Create(settings);
        image.DisplayName = "mutated.png";
        var persisted = snapshot.ProjectThreads.Single();
        Assert(persisted.QueuedFollowUps.Single().Images.Single().DisplayName == "image.png", "queue image deep copied");
        Assert(persisted.ConversationTurns.Single().UserImages.Single().DisplayName == "image.png", "turn image deep copied");
        Assert(snapshot.ComposerAttachmentDrafts.Single().Images.Single().DisplayName == "image.png", "draft image deep copied");
        return Task.CompletedTask;
    }

    private static Task ComposerAttachmentStateValidatesCapabilityAsync()
    {
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false);
        var image = Reference("objects/aa/image.png");
        viewModel.AddAttachment(image);
        Assert(viewModel.HasAttachments, "composer exposes attachment state");

        viewModel.ApplyModelCatalog(
        [
            new CodexModelOption(
                "text", "gpt-text", "Text", "Text only", true, false, null, [], [], null,
                InputModalities: [CodexInputModality.Text])
        ], null);
        Assert(!viewModel.CanSubmitAttachments, "text-only model blocks image submission");
        Assert(viewModel.AttachmentValidationMessage.Contains("does not accept image", StringComparison.OrdinalIgnoreCase), "capability error is actionable");

        viewModel.RemoveAttachmentCommand.Execute(image);
        Assert(!viewModel.HasAttachments && viewModel.CanSubmitAttachments, "removing image clears capability error");
        return Task.CompletedTask;
    }

    private static Task TaskViewExposesAttachmentSurfaceAsync() => WpfTestHost.RunAsync(() =>
    {
        var resources = Application.Current.Resources;
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["Card"] = new Style(typeof(Border));
        resources["StatePill"] = new Style(typeof(Border));
        resources["ConversationTurnCard"] = new Style(typeof(Border));
        resources["ConversationUserSurface"] = new Style(typeof(Border));
        resources["ConversationAssistantSurface"] = new Style(typeof(Border));
        resources["ConversationActivitySurface"] = new Style(typeof(Border));
        resources["CompactButton"] = new Style(typeof(Button));
        resources["RunTaskButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = new Style(typeof(TextBlock));
        resources["ConversationBodyText"] = new Style(typeof(TextBlock));
        resources["ConversationRoleText"] = new Style(typeof(TextBlock));
        resources["ConversationMetadataText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityTitleText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityDetailText"] = new Style(typeof(TextBlock));
        var view = new TaskView();
        Assert(view.FindName("AttachImagesButton") is FrameworkElement, "attach button exists");
        Assert(view.FindName("AttachmentPreviewList") is FrameworkElement, "attachment preview list exists");
        Assert(view.FindName("ComposerDropTarget") is FrameworkElement { AllowDrop: true }, "composer accepts drops");
    });

    private static async Task InitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
        await transport.WaitForClientMessageCountAsync(2);
    }

    private static AttachmentReference Reference(string storageKey) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        StorageKey = storageKey,
        DisplayName = "image.png",
        MediaType = "image/png",
        ByteLength = TinyPng.Length,
        PixelWidth = 1,
        PixelHeight = 1,
        ContentSha256 = new string('a', 64)
    };

    private static QueuedTurnOptionsSnapshot Options(string workspacePath) => new()
    {
        WorkspacePath = workspacePath,
        Model = "gpt-vision",
        ServiceTier = CodexServiceTierSelection.Standard
    };

    private static string? ReadString(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
        }
        return current?.GetValue<string>();
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
