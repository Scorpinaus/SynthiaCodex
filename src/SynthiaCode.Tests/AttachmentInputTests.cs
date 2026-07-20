using System.Text.Json.Nodes;
using System.Text.Json;
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
        ("attachment protocol serializes file and folder mentions", ProtocolSerializesMentionsAsync),
        ("model catalog advertises image input capability", ModelCatalogAdvertisesImageCapabilityAsync),
        ("managed attachment store copies validates and deduplicates images", StoreCopiesValidatesAndDeduplicatesAsync),
        ("managed attachment store snapshots external files and folders", StoreSnapshotsExternalFilesAndFoldersAsync),
        ("external snapshots build file folder and image prompt parts", ExternalSnapshotsBuildPromptPartsAsync),
        ("workspace attachment resolver contains files and folders", WorkspaceResolverContainsReferencesAsync),
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

    private static async Task ProtocolSerializesMentionsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("attachment_tests", "Attachment Tests", "1.0"));
        await InitializeAsync(client, transport);

        var filePath = Path.GetFullPath(@"C:\Repo\src\App.cs");
        var folderPath = Path.GetFullPath(@"C:\Repo\src");
        var startTask = client.StartTurnAsync(new CodexTurnStartRequest(
            "thr_mentions",
            [
                new CodexTextInput("Review these"),
                new CodexMentionInput("src/App.cs", filePath),
                new CodexMentionInput("src", folderPath)
            ],
            @"C:\Repo",
            CodexSandbox.WorkspaceWrite));

        await transport.WaitForClientMessageCountAsync(3);
        var start = JsonNode.Parse(transport.ClientMessages[2])!.AsObject();
        Assert(ReadString(start, "params.input.1.type") == "mention", "file mention type");
        Assert(ReadString(start, "params.input.1.name") == "src/App.cs", "file mention name");
        Assert(ReadString(start, "params.input.1.path") == filePath, "file mention path");
        Assert(ReadString(start, "params.input.2.type") == "mention", "folder mention type");
        transport.ServerSend("""{"id":1,"result":{"turn":{"id":"turn_mentions"}}}""");
        await startTask;

        var steerTask = client.SteerTurnAsync(new CodexTurnSteerRequest(
            "thr_mentions",
            "turn_mentions",
            [new CodexMentionInput("src/App.cs", filePath)]));
        await transport.WaitForClientMessageCountAsync(4);
        var steer = JsonNode.Parse(transport.ClientMessages[3])!.AsObject();
        Assert(ReadString(steer, "params.input.0.type") == "mention", "steer mention type");
        Assert(ReadString(steer, "params.input.0.path") == filePath, "steer mention path");
        transport.ServerSend("""{"id":2,"result":{"turnId":"turn_mentions"}}""");
        await steerTask;
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

    private static async Task StoreSnapshotsExternalFilesAndFoldersAsync()
    {
        using var temp = TempWorkspace.Create();
        var sourceFile = Path.Combine(temp.Root, "article.md");
        await File.WriteAllTextAsync(sourceFile, "# External attachment\n");
        var renamedSourceFile = Path.Combine(temp.Root, "renamed.data");
        File.Copy(sourceFile, renamedSourceFile);
        var sourceFolder = Path.Combine(temp.Root, "reference-folder");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "nested"));
        Directory.CreateDirectory(Path.Combine(sourceFolder, "empty"));
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "nested", "beta.md"), "beta");
        var store = new LocalAttachmentStore(Path.Combine(temp.Root, "managed"), new TestLogger());

        var firstFile = await store.ImportExternalFileAsync(sourceFile);
        var duplicateFile = await store.ImportExternalFileAsync(sourceFile);
        var renamedFile = await store.ImportExternalFileAsync(renamedSourceFile);
        var firstFolder = await store.ImportFolderAsync(sourceFolder);
        var duplicateFolder = await store.ImportFolderAsync(sourceFolder);
        File.Delete(sourceFile);
        Directory.Delete(sourceFolder, recursive: true);

        Assert(firstFile.Kind == AttachmentKind.File, "external file becomes managed file");
        Assert(firstFile.SourceKind == AttachmentSourceKind.ManagedCopy, "external file source is managed");
        Assert(firstFile.StorageKey == duplicateFile.StorageKey, "external file content deduplicates");
        Assert(firstFile.StorageKey == renamedFile.StorageKey, "external file deduplication ignores name and extension");
        Assert(File.Exists(store.ResolvePath(firstFile)), "managed file survives source deletion");
        Assert(firstFolder.Kind == AttachmentKind.Folder, "external folder becomes managed folder");
        Assert(firstFolder.SourceKind == AttachmentSourceKind.ManagedCopy, "external folder source is managed");
        Assert(firstFolder.StorageKey == duplicateFolder.StorageKey, "external folder content deduplicates");
        Assert(firstFolder.SnapshotFileCount == 2, "folder snapshot records file count");
        Assert(firstFolder.SnapshotByteLength == 9, "folder snapshot records total bytes");
        var folderPath = store.ResolvePath(firstFolder);
        Assert(File.Exists(Path.Combine(folderPath, "alpha.txt")), "folder snapshot retains root file");
        Assert(File.Exists(Path.Combine(folderPath, "nested", "beta.md")), "folder snapshot retains nested file");
        Assert(Directory.Exists(Path.Combine(folderPath, "empty")), "folder snapshot retains empty directories");

        var oversized = Path.Combine(temp.Root, "oversized.bin");
        await using (var oversizedStream = File.Create(oversized))
        {
            oversizedStream.SetLength(AttachmentLimits.MaximumBytesPerExternalFile + 1);
        }
        await AssertThrowsAsync<InvalidDataException>(
            () => store.ImportExternalFileAsync(oversized),
            "oversized external file is rejected before import");

        var orphanSource = Path.Combine(temp.Root, "orphan.txt");
        await File.WriteAllTextAsync(orphanSource, "orphan");
        var orphan = await store.ImportExternalFileAsync(orphanSource);
        var orphanPath = store.ResolvePath(orphan);
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddDays(-8));
        await store.CleanupAsync([firstFile.StorageKey, firstFolder.StorageKey]);
        Assert(File.Exists(store.ResolvePath(firstFile)), "cleanup keeps referenced managed file");
        Assert(File.Exists(Path.Combine(folderPath, "nested", "beta.md")), "cleanup keeps referenced folder contents");
        Assert(Directory.Exists(Path.Combine(folderPath, "empty")), "cleanup keeps referenced empty directories");
        Assert(!File.Exists(orphanPath), "cleanup removes expired unreferenced managed file");

        var queue = new CodexFollowUpQueue();
        var queued = queue.Enqueue("Review snapshots", Options(temp.Root), [firstFile, firstFolder]);
        Assert(queued.Attachments.Count == 2, "queue accepts managed file and folder snapshots");
        Assert(queued.Attachments[1].SnapshotFileCount == 2, "queue preserves folder metadata");
    }

    private static async Task ExternalSnapshotsBuildPromptPartsAsync()
    {
        using var temp = TempWorkspace.Create();
        var sourceFile = Path.Combine(temp.Root, "outside.txt");
        var sourceFolder = Path.Combine(temp.Root, "outside-folder");
        var sourceImage = Path.Combine(temp.Root, "outside.png");
        await File.WriteAllTextAsync(sourceFile, "outside");
        Directory.CreateDirectory(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "nested.txt"), "nested");
        await File.WriteAllBytesAsync(sourceImage, TinyPng);
        var store = new LocalAttachmentStore(Path.Combine(temp.Root, "managed"), new TestLogger());
        var file = await store.ImportExternalFileAsync(sourceFile);
        var folder = await store.ImportFolderAsync(sourceFolder);
        var image = await store.ImportFileAsync(sourceImage);
        var builder = new AttachmentPromptInputBuilder(store, new WorkspaceAttachmentResolver());

        var inputs = builder.Build("Review external inputs", [file, folder, image], temp.Root);

        Assert(inputs[0] is CodexTextInput, "prompt text remains first");
        Assert(inputs[1] is CodexMentionInput fileMention && fileMention.Path == store.ResolvePath(file), "managed file becomes mention");
        Assert(inputs[2] is CodexMentionInput folderMention && folderMention.Path == store.ResolvePath(folder), "managed folder becomes mention");
        Assert(inputs[3] is CodexLocalImageInput imageInput && imageInput.Path == store.ResolvePath(image), "managed image remains local image");
    }

    private static Task WorkspaceResolverContainsReferencesAsync()
    {
        using var workspace = TempWorkspace.Create();
        var sourceFolder = Path.Combine(workspace.Root, "src");
        Directory.CreateDirectory(sourceFolder);
        var sourceFile = Path.Combine(sourceFolder, "App.cs");
        File.WriteAllText(sourceFile, "class App { }");
        var sibling = Path.Combine(Path.GetDirectoryName(workspace.Root)!, Path.GetFileName(workspace.Root) + "-sibling");
        Directory.CreateDirectory(sibling);
        var outsideFile = Path.Combine(sibling, "outside.cs");
        File.WriteAllText(outsideFile, "outside");

        try
        {
            var resolver = new WorkspaceAttachmentResolver();
            Assert(resolver.IsWithinWorkspace(workspace.Root, sourceFile), "inside file is classified as workspace content");
            Assert(!resolver.IsWithinWorkspace(workspace.Root, outsideFile), "outside file is classified for managed snapshot import");
            var file = resolver.Resolve(workspace.Root, sourceFile, AttachmentKind.File);
            var folder = resolver.Resolve(workspace.Root, sourceFolder, AttachmentKind.Folder);
            Assert(file.Kind == AttachmentKind.File && file.SourceKind == AttachmentSourceKind.WorkspaceReference, "file reference kind");
            Assert(file.WorkspaceRelativePath == "src/App.cs", "file path stored relative");
            Assert(folder.Kind == AttachmentKind.Folder && folder.WorkspaceRelativePath == "src", "folder path stored relative");
            AssertThrows<InvalidDataException>(
                () => resolver.Resolve(workspace.Root, outsideFile, AttachmentKind.File),
                "sibling prefix path rejected");
            AssertThrows<InvalidDataException>(
                () => resolver.Resolve(workspace.Root, workspace.Root, AttachmentKind.Folder),
                "workspace root folder rejected");
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task ReferencesSurviveQueueAndSettingsSnapshotsAsync()
    {
        var image = Reference("objects/aa/image.png");
        var file = new AttachmentReference
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = AttachmentKind.File,
            SourceKind = AttachmentSourceKind.WorkspaceReference,
            DisplayName = "App.cs",
            WorkspaceRelativePath = "src/App.cs"
        };
        var queue = new CodexFollowUpQueue();
        var queued = queue.Enqueue("Inspect it", Options(@"D:\Repo"), [image, file]);
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
                            UserAttachments = [image, file]
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
                    Attachments = [image, file]
                }
            ]
        };

        var snapshot = AppSettingsSnapshot.Create(settings);
        image.DisplayName = "mutated.png";
        var persisted = snapshot.ProjectThreads.Single();
        Assert(persisted.QueuedFollowUps.Single().Attachments[0].DisplayName == "image.png", "queue image deep copied");
        Assert(persisted.QueuedFollowUps.Single().Attachments[1].WorkspaceRelativePath == "src/App.cs", "queue file copied");
        Assert(persisted.ConversationTurns.Single().UserAttachments[0].DisplayName == "image.png", "turn image deep copied");
        Assert(snapshot.ComposerAttachmentDrafts.Single().Attachments[1].WorkspaceRelativePath == "src/App.cs", "draft file deep copied");
        var json = JsonSerializer.Serialize(snapshot);
        Assert(json.Contains("\"Attachments\"", StringComparison.Ordinal), "v2 attachment property is serialized");
        Assert(json.Contains("\"UserAttachments\"", StringComparison.Ordinal), "v2 turn attachment property is serialized");
        Assert(!json.Contains("\"Images\"", StringComparison.Ordinal), "legacy image property is not written");
        Assert(!json.Contains("\"UserImages\"", StringComparison.Ordinal), "legacy turn image property is not written");

        var legacy = JsonSerializer.Deserialize<AppSettings>(
            """{"ComposerAttachmentDrafts":[{"ProjectPath":"D:\\Repo","ThreadId":"thr","Images":[{"Id":"legacy","StorageKey":"objects/legacy.png","DisplayName":"legacy.png","ByteLength":12}]}]}""")!;
        var migrated = legacy.ComposerAttachmentDrafts.Single().Attachments.Single();
        Assert(migrated.Kind == AttachmentKind.Image, "legacy attachment defaults to image");
        Assert(migrated.SourceKind == AttachmentSourceKind.ManagedCopy, "legacy attachment defaults to managed copy");
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

        var file = new AttachmentReference
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = AttachmentKind.File,
            SourceKind = AttachmentSourceKind.WorkspaceReference,
            DisplayName = "App.cs",
            WorkspaceRelativePath = "src/App.cs"
        };
        viewModel.AddAttachment(file);
        Assert(viewModel.CanSubmitAttachments, "text-only model permits workspace file mentions");
        viewModel.ClearAttachments();
        viewModel.AddAttachment(image);
        viewModel.AddAttachment(new AttachmentReference
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = AttachmentKind.File,
            SourceKind = AttachmentSourceKind.ManagedCopy,
            StorageKey = "objects/files/aa/file.png",
            DisplayName = "file.png",
            ByteLength = image.ByteLength,
            SnapshotFileCount = 1,
            SnapshotByteLength = image.ByteLength,
            ContentSha256 = image.ContentSha256
        });
        Assert(viewModel.Attachments.Count == 2, "different attachment kinds are not deduplicated by hash alone");
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
        resources["CompactButton"] = new Style(typeof(Button));
        resources["RunTaskButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = new Style(typeof(TextBlock));
        resources["ConversationBodyText"] = new Style(typeof(TextBlock));
        resources["ConversationRoleText"] = new Style(typeof(TextBlock));
        resources["ConversationMetadataText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityTitleText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityDetailText"] = new Style(typeof(TextBlock));
        var view = new TaskView();
        var attachButton = view.FindName("AttachButton") as Button;
        Assert(attachButton is not null, "combined attach button exists");
        Assert(attachButton!.ContextMenu?.Items.Count == 3, "combined attach menu offers three attachment kinds");
        var attachOptions = attachButton.ContextMenu!.Items
            .OfType<MenuItem>()
            .Select(item => item.Header?.ToString())
            .ToArray();
        Assert(attachOptions.SequenceEqual(["Images...", "Files...", "Folder..."]), "attach menu labels are ordered and actionable");
        attachButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(attachButton.ContextMenu.IsOpen, "combined attach button opens its menu");
        attachButton.ContextMenu.IsOpen = false;
        Assert(view.FindName("AttachImagesButton") is null, "separate image button is removed");
        Assert(view.FindName("AttachFilesButton") is null, "separate file button is removed");
        Assert(view.FindName("AttachFolderButton") is null, "separate folder button is removed");
        var attachmentPanel = view.FindName("AttachmentPreviewPanel") as FrameworkElement;
        var promptPanel = view.FindName("PromptInputPanel") as FrameworkElement;
        Assert(attachmentPanel is not null && promptPanel is not null, "composer layout elements exist");
        Assert(Grid.GetRow(attachmentPanel!) < Grid.GetRow(promptPanel!), "attachment previews appear above the prompt field");
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
        Kind = AttachmentKind.Image,
        SourceKind = AttachmentSourceKind.ManagedCopy,
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

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
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
