using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Text.Json.Nodes;
using SynthiaCode.App.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;

internal static class MarkdownLinkTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("markdown renderer formats strong assistant text", RendererFormatsStrongTextAsync),
        ("markdown renderer formats emphasis code strike and escapes", RendererFormatsEmphasisCodeStrikeAndEscapesAsync),
        ("markdown renderer formats headings lists tasks and rules", RendererFormatsHeadingsListsTasksAndRulesAsync),
        ("markdown renderer formats quotes and fenced code", RendererFormatsQuotesAndFencedCodeAsync),
        ("markdown renderer lays out pipe tables", RendererLaysOutPipeTablesAsync),
        ("markdown link renderer recognizes safe assistant links", RendererRecognizesSafeLinksAsync),
        ("markdown renderer embeds generated local images", RendererEmbedsGeneratedLocalImagesAsync),
        ("image-generation notifications render an inline chat preview", ImageGenerationNotificationsRenderInlinePreviewAsync),
        ("generated image viewer loads an expanded image", GeneratedImageViewerLoadsExpandedImageAsync),
        ("generated image editor renders rectangle and freehand region guides", GeneratedImageEditorRendersRegionGuidesAsync),
        ("generated image editor increases and decreases the draw brush size", GeneratedImageEditorAdjustsDrawBrushSizeAsync),
        ("markdown link renderer preserves unsafe and malformed links", RendererPreservesUnsafeAndMalformedLinksAsync),
        ("markdown link renderer routes link activation through its command", RendererRoutesLinkActivationAsync),
        ("external link policy permits only web destinations", ExternalLinkPolicyPermitsOnlyWebDestinations)
    ];

    private static Task RendererFormatsStrongTextAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = "The **latest flagship** and __best value__ models; `**literal code**`; **[open docs](https://example.com/docs)**."
        };

        var strongSpans = renderer.Inlines.OfType<Bold>().ToArray();
        Assert(strongSpans.Length == 3, "asterisk and underscore strong markers render as bold spans");
        Assert(InlineText(strongSpans[0]) == "latest flagship", "asterisk markers are removed from bold text");
        Assert(InlineText(strongSpans[1]) == "best value", "underscore markers are removed from bold text");
        Assert(
            strongSpans[2].Inlines.OfType<Hyperlink>().Single().NavigateUri?.AbsoluteUri == "https://example.com/docs",
            "links remain interactive inside bold text");
        var code = renderer.Inlines.OfType<Run>().Single(run => AutomationProperties.GetName(run) == "Inline code");
        Assert(code.Text == "**literal code**", "inline code removes its delimiters but preserves nested markdown literally");

        const string unmatched = "Keep an unmatched **marker visible";
        var unmatchedRenderer = new MarkdownTextBlock { Markdown = unmatched };
        Assert(InlineText(unmatchedRenderer) == unmatched, "unmatched strong syntax remains visible");
    });

    private static Task RendererFormatsEmphasisCodeStrikeAndEscapesAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = "Use *fast* or _local_, avoid ~~legacy~~, run `dotnet test`, combine ***both***, and keep \\*literal\\*."
        };

        var emphasis = renderer.Inlines.OfType<Italic>().ToArray();
        Assert(emphasis.Length == 2, "asterisk and underscore emphasis markers render as italic spans");
        Assert(InlineText(emphasis[0]) == "fast" && InlineText(emphasis[1]) == "local", "emphasis delimiters are removed");
        var strike = renderer.Inlines.OfType<Span>()
            .Single(span => AutomationProperties.GetName(span) == "Strikethrough");
        Assert(InlineText(strike) == "legacy", "strikethrough delimiters are removed");
        Assert(strike.TextDecorations.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough), "strikethrough decoration is applied");
        var code = renderer.Inlines.OfType<Run>().Single(run => AutomationProperties.GetName(run) == "Inline code");
        Assert(code.Text == "dotnet test", "inline code delimiters are removed");
        var combined = renderer.Inlines.OfType<Bold>()
            .Single(bold => AutomationProperties.GetName(bold) == "Bold italic");
        Assert(InlineText(combined.Inlines.OfType<Italic>().Single()) == "both", "triple markers combine bold and italic formatting");
        Assert(InlineText(renderer).EndsWith("keep *literal*.", StringComparison.Ordinal), "escaped markdown punctuation remains literal");
    });

    private static Task RendererFormatsHeadingsListsTasksAndRulesAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = """
                # Model summary
                ### Details with **bold**

                - First item
                * Second item
                1. Ordered item
                - [x] Complete task
                - [ ] Pending task

                ---
                """
        };

        var firstHeading = renderer.Inlines.OfType<Span>()
            .Single(span => AutomationProperties.GetName(span) == "Markdown heading level 1");
        var thirdHeading = renderer.Inlines.OfType<Span>()
            .Single(span => AutomationProperties.GetName(span) == "Markdown heading level 3");
        Assert(firstHeading.FontWeight == FontWeights.Bold, "headings use bold text");
        Assert(firstHeading.FontSize > thirdHeading.FontSize && thirdHeading.FontSize > renderer.FontSize, "heading levels use descending display sizes");
        Assert(thirdHeading.Inlines.OfType<Bold>().Any(), "inline formatting remains active inside headings");

        var unordered = renderer.Inlines.OfType<Span>()
            .Where(span => AutomationProperties.GetName(span) == "Markdown unordered list item")
            .ToArray();
        Assert(unordered.Length == 2 && unordered.All(item => InlineText(item).StartsWith("• ", StringComparison.Ordinal)), "unordered lists use bullet prefixes");
        var ordered = renderer.Inlines.OfType<Span>()
            .Single(span => AutomationProperties.GetName(span) == "Markdown ordered list item");
        Assert(InlineText(ordered).StartsWith("1. ", StringComparison.Ordinal), "ordered lists retain their ordinal");
        var tasks = renderer.Inlines.OfType<Span>()
            .Where(span => AutomationProperties.GetName(span) == "Markdown task list item")
            .Select(InlineText)
            .ToArray();
        Assert(tasks.SequenceEqual(["☑ Complete task", "☐ Pending task"]), "task lists expose checked and unchecked states");

        var rule = renderer.Inlines.OfType<InlineUIContainer>()
            .Single(container => AutomationProperties.GetName(container.Child) == "Markdown horizontal rule");
        Assert(rule.Child is Border { Height: 1 }, "horizontal rules render as a one-pixel divider");
    });

    private static Task RendererFormatsQuotesAndFencedCodeAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = """
                > **Note:** Prefer the hosted model.
                > Continue only after approval.

                ```csharp
                var model = "**literal markdown**";
                Console.WriteLine(model);
                ```
                """
        };

        var quote = (Border)renderer.Inlines.OfType<InlineUIContainer>()
            .Single(container => AutomationProperties.GetName(container.Child) == "Markdown block quote")
            .Child;
        var quoteText = (MarkdownTextBlock)quote.Child;
        Assert(quoteText.Inlines.OfType<Bold>().Any(), "inline markdown remains active inside block quotes");
        Assert(InlineText(quoteText).Contains("Continue only after approval.", StringComparison.Ordinal), "multi-line block quotes remain grouped");

        var codeBlock = (Border)renderer.Inlines.OfType<InlineUIContainer>()
            .Single(container => AutomationProperties.GetName(container.Child) == "Markdown fenced code block")
            .Child;
        var codeScroller = ((StackPanel)codeBlock.Child).Children.OfType<ScrollViewer>().Single();
        var codeText = (TextBlock)codeScroller.Content;
        Assert(codeScroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "fenced code supports horizontal scrolling");
        Assert(InlineText(codeText).Contains("**literal markdown**", StringComparison.Ordinal), "fenced code preserves markdown syntax literally");
        Assert(!codeText.Inlines.OfType<Bold>().Any(), "fenced code does not apply inline formatting");

        var tildeRenderer = new MarkdownTextBlock { Markdown = "~~~text\ntilde fence\n~~~" };
        Assert(
            tildeRenderer.Inlines.OfType<InlineUIContainer>()
                .Any(container => AutomationProperties.GetName(container.Child) == "Markdown fenced code block"),
            "tilde fenced code blocks are supported");

        const string unclosed = "```csharp\nvar value = 1;";
        var unclosedRenderer = new MarkdownTextBlock { Markdown = unclosed };
        Assert(!unclosedRenderer.Inlines.OfType<InlineUIContainer>().Any(), "unclosed code fences do not become code blocks");
        Assert(InlineText(unclosedRenderer) == unclosed, "unclosed code fences remain visible verbatim");
    });

    private static Task RendererLaysOutPipeTablesAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Width = 720,
            Markdown = """
                Latest models:

                | Model | Availability | Best suited for |
                |---|:---:|---:|
                | **Qwen3.7-Max** | Hosted/API | General agents |
                | Qwen3.6-27B | Open weights | [Local use](https://example.com/models) |
                | Qwen3.6-4B | Open \| local | `A|B` benchmark |

                Choose based on deployment needs.
                """
        };

        var table = (Grid)renderer.Inlines.OfType<InlineUIContainer>()
            .Single(container => AutomationProperties.GetName(container.Child) == "Markdown table")
            .Child;
        Assert(table.ColumnDefinitions.Count == 3, "table creates one grid column per markdown column");
        Assert(table.RowDefinitions.Count == 4, "delimiter row is omitted from the rendered table");

        var cells = table.Children.OfType<Border>()
            .ToDictionary(cell => (Grid.GetRow(cell), Grid.GetColumn(cell)));
        var headerCell = (MarkdownTextBlock)cells[(0, 0)].Child;
        var boldModelCell = (MarkdownTextBlock)cells[(1, 0)].Child;
        var linkedCell = (MarkdownTextBlock)cells[(2, 2)].Child;
        Assert(headerCell.FontWeight == FontWeights.SemiBold, "table headers use emphasized text");
        Assert(((MarkdownTextBlock)cells[(0, 1)].Child).TextAlignment == TextAlignment.Center, "center table alignment applies to headers");
        Assert(((MarkdownTextBlock)cells[(1, 1)].Child).TextAlignment == TextAlignment.Center, "center table alignment applies to body cells");
        Assert(linkedCell.TextAlignment == TextAlignment.Right, "right table alignment applies to body cells");
        Assert(InlineText(boldModelCell.Inlines.OfType<Bold>().Single()) == "Qwen3.7-Max", "bold formatting works inside table cells");
        Assert(
            linkedCell.Inlines.OfType<Hyperlink>().Single().NavigateUri?.AbsoluteUri == "https://example.com/models",
            "safe links remain interactive inside table cells");
        Assert(InlineText((MarkdownTextBlock)cells[(3, 1)].Child) == "Open | local", "escaped pipes stay inside their table cell");
        var tableCode = (MarkdownTextBlock)cells[(3, 2)].Child;
        Assert(tableCode.Inlines.OfType<Run>().Single(run => AutomationProperties.GetName(run) == "Inline code").Text == "A|B", "code-span pipes do not split table cells");

        const string malformed = "| Model | Availability |\n| -- | -- |\n| Qwen | Hosted |";
        var malformedRenderer = new MarkdownTextBlock { Markdown = malformed };
        Assert(!malformedRenderer.Inlines.OfType<InlineUIContainer>().Any(), "invalid delimiter rows do not become tables");
        Assert(InlineText(malformedRenderer) == malformed, "invalid table-like text remains visible verbatim");
    });

    private static Task RendererRecognizesSafeLinksAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = "Read [release notes](https://example.com/releases), <https://reference.example.com>, or https://docs.example.com/guide."
        };

        var links = renderer.Inlines.OfType<Hyperlink>().ToArray();
        Assert(links.Length == 3, "three safe link forms are rendered");
        Assert(links[0].NavigateUri?.AbsoluteUri == "https://example.com/releases", "markdown destination is retained");
        Assert(InlineText(links[0]) == "release notes", "markdown label is rendered without syntax");
        Assert(links[1].NavigateUri?.AbsoluteUri == "https://reference.example.com/", "angle-bracket autolink is rendered");
        Assert(links[2].NavigateUri?.AbsoluteUri == "https://docs.example.com/guide", "bare URL excludes trailing punctuation");
        Assert(InlineText(links[2]) == "https://docs.example.com/guide", "bare URL is its own label");
    });

    private static Task RendererEmbedsGeneratedLocalImagesAsync() => RunOnStaAsync(() =>
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"synthiacode-markdown-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "generated-infographic.png");
        var textPath = Path.Combine(tempDirectory, "notes.txt");
        File.WriteAllBytes(
            imagePath,
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z6JkAAAAASUVORK5CYII="));
        File.WriteAllText(textPath, "not an image");

        try
        {
            Uri? activatedUri = null;
            string? editedPath = null;
            var renderer = new MarkdownTextBlock
            {
                Markdown = $"Created the infographic.{Environment.NewLine}{Environment.NewLine}[Download the PNG]({imagePath})",
                LinkCommand = new RelayCommand(parameter => activatedUri = parameter as Uri),
                EditImageCommand = new RelayCommand(parameter => editedPath = parameter as string)
            };

            var preview = renderer.Inlines.OfType<InlineUIContainer>()
                .Single(container => AutomationProperties.GetName(container.Child).StartsWith("Generated image preview:", StringComparison.Ordinal));
            var card = preview.Child as Border
                ?? throw new InvalidOperationException("generated image preview does not use a bounded card");
            var content = card.Child as StackPanel
                ?? throw new InvalidOperationException("generated image preview content was not created");
            var previewButton = content.Children.OfType<Button>().Single(button => button.Content is Image);
            var editButton = content.Children.OfType<Button>().Single(button => button.Content is string);
            var image = previewButton.Content as Image
                ?? throw new InvalidOperationException("generated image preview button does not contain the image");
            var link = ((TextBlock)content.Children.OfType<TextBlock>().Single()).Inlines.OfType<Hyperlink>().Single();

            Assert(image.Source is not null, "the generated image is decoded into the transcript");
            Assert(image.MaxWidth <= 720 && image.MaxHeight <= 480, "the generated image preview is bounded");
            Assert(
                AutomationProperties.GetName(previewButton) == "Edit generated image: Download the PNG",
                "the generated image preview exposes an accessible edit action");
            Assert(link.NavigateUri?.IsFile == true, "the embedded download link targets the local image");
            Assert(InlineText(link) == "Download the PNG", "the assistant-provided image label remains clickable");
            Assert((string)editButton.Content == "Edit image", "the generated-image card exposes an edit action");
            Assert(
                AutomationProperties.GetName(editButton) == "Edit generated image: Download the PNG",
                "the generated-image edit action has an accessible name");

            editButton.Command.Execute(editButton.CommandParameter);
            Assert(
                string.Equals(editedPath, imagePath, StringComparison.OrdinalIgnoreCase),
                "the edit action routes the generated local path through its command");

            editedPath = null;
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(
                string.Equals(editedPath, imagePath, StringComparison.OrdinalIgnoreCase),
                "clicking the generated image starts the edit workflow");

            activatedUri = null;
            link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri!, null));
            Assert(
                activatedUri?.IsFile == true &&
                string.Equals(activatedUri.LocalPath, imagePath, StringComparison.OrdinalIgnoreCase),
                "local image link activation is routed through the transcript command");

            string? revealedPath = null;
            var taskWorkspace = WorkspaceActionStubs.CreateTaskViewModel(WorkspaceActionStubs.Task(
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => false,
                () => false,
                showLocalImage: path => revealedPath = path,
                editGeneratedImage: path =>
                {
                    editedPath = path;
                    return Task.CompletedTask;
                }));
            Assert(taskWorkspace.OpenExternalUriCommand.CanExecute(link.NavigateUri), "the transcript command accepts an existing local image");
            taskWorkspace.OpenExternalUriCommand.Execute(link.NavigateUri);
            Assert(
                string.Equals(revealedPath, imagePath, StringComparison.OrdinalIgnoreCase),
                "the transcript command opens the expanded generated image through the interaction service");
            Assert(taskWorkspace.EditGeneratedImageCommand.CanExecute(imagePath), "an idle conversation can edit an available generated image");
            editedPath = null;
            ((AsyncRelayCommand)taskWorkspace.EditGeneratedImageCommand).ExecuteAsync(imagePath).GetAwaiter().GetResult();
            Assert(
                string.Equals(editedPath, imagePath, StringComparison.OrdinalIgnoreCase),
                "the task workspace routes generated-image edits through its composer action contract");
            taskWorkspace.IsTurnRunning = true;
            Assert(!taskWorkspace.EditGeneratedImageCommand.CanExecute(imagePath), "image editing is disabled while a turn is running");
            taskWorkspace.IsTurnRunning = false;
            Assert(!taskWorkspace.EditGeneratedImageCommand.CanExecute(textPath + ".missing.png"), "image editing is disabled when the source is missing");

            var unsupported = new MarkdownTextBlock { Markdown = $"[Open notes]({textPath})" };
            Assert(!unsupported.Inlines.OfType<InlineUIContainer>().Any(), "non-image local files are not embedded");
            Assert(InlineText(unsupported) == $"[Open notes]({textPath})", "unsupported local files remain visible as literal text");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    });

    private static Task ImageGenerationNotificationsRenderInlinePreviewAsync() => RunOnStaAsync(() =>
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"synthiacode-image-event-({Guid.NewGuid():N})");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "generated infographic (1).png");
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            864,
            1821,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            new byte[864 * 1821 * 4],
            864 * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = File.Create(imagePath))
        {
            encoder.Save(stream);
        }

        try
        {
            var service = new CodexThreadService();
            service.Restore("thread-image", null, null, null);
            service.BeginTurn("Generate an infographic");
            service.BindPendingTurn("turn-image");
            service.ApplyNotification(CodexAppServerNotification.Decode(new AppServerNotification(
                "item/completed",
                new JsonObject
                {
                    ["threadId"] = "thread-image",
                    ["turnId"] = "turn-image",
                    ["item"] = new JsonObject
                    {
                        ["id"] = "image-1",
                        ["type"] = "imageGeneration",
                        ["status"] = "completed",
                        ["result"] = "generated",
                        ["savedPath"] = imagePath
                    }
                })));

            var renderer = new MarkdownTextBlock
            {
                Markdown = service.ActiveConversationTurn!.AssistantResponseDisplay,
                Width = 760,
                LineHeight = 22,
                TextWrapping = TextWrapping.Wrap
            };
            var preview = renderer.Inlines.OfType<InlineUIContainer>()
                .Single(container =>
                    AutomationProperties.GetName(container.Child).StartsWith(
                        "Generated image preview:",
                        StringComparison.Ordinal));
            var image = ((StackPanel)((Border)preview.Child).Child)
                .Children
                .OfType<Button>()
                .Select(button => button.Content)
                .OfType<Image>()
                .Single();

            Assert(image.Source is not null, "the canonical image-generation event decodes into an inline transcript preview");
            renderer.Measure(new Size(760, double.PositiveInfinity));
            renderer.Arrange(new Rect(0, 0, 760, renderer.DesiredSize.Height));
            renderer.UpdateLayout();
            renderer.Measure(new Size(760, double.PositiveInfinity));
            renderer.Arrange(new Rect(0, 0, 760, renderer.DesiredSize.Height));
            Assert(
                preview.Child.RenderSize.Height >= 400,
                $"the generated-image card receives visible portrait layout height (actual {preview.Child.RenderSize})");
            Assert(
                renderer.RenderSize.Height >= 400,
                $"the transcript Markdown surface does not clip the generated-image card (actual {renderer.RenderSize})");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    });

    private static Task GeneratedImageViewerLoadsExpandedImageAsync() => RunOnStaAsync(() =>
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"synthiacode-expanded-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "expanded-infographic.png");
        File.WriteAllBytes(
            imagePath,
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z6JkAAAAASUVORK5CYII="));

        try
        {
            var viewer = new GeneratedImagePreviewWindow(imagePath);
            Assert(viewer.Title.Contains("expanded-infographic.png", StringComparison.Ordinal), "the expanded viewer identifies the image");
            Assert(viewer.Width > 720 && viewer.Height > 480, "the expanded viewer is larger than the transcript preview");
            Assert(viewer.PreviewImage.Source is not null, "the expanded viewer decodes the full image");
            Assert(viewer.PreviewImage.Stretch == System.Windows.Media.Stretch.Uniform, "the expanded image retains its aspect ratio");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    });

    private static Task GeneratedImageEditorRendersRegionGuidesAsync() => RunOnStaAsync(() =>
    {
        const int width = 100;
        const int height = 80;
        const int stride = width * 4;
        var sourcePixels = new byte[stride * height];
        for (var index = 0; index < sourcePixels.Length; index += 4)
        {
            sourcePixels[index] = 180;
            sourcePixels[index + 1] = 40;
            sourcePixels[index + 2] = 20;
            sourcePixels[index + 3] = 255;
        }

        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            sourcePixels,
            stride);
        source.Freeze();

        var rectangleGuide = GeneratedImageRegionGuideRenderer.RenderPng(
            source,
            GeneratedImageEditRegion.Rectangle(
                new NormalizedImagePoint(0.25, 0.25),
                new NormalizedImagePoint(0.75, 0.75)));
        var freehandGuide = GeneratedImageRegionGuideRenderer.RenderPng(
            source,
            GeneratedImageEditRegion.Freehand(
            [
                new NormalizedImagePoint(0.2, 0.2),
                new NormalizedImagePoint(0.5, 0.5),
                new NormalizedImagePoint(0.8, 0.7)
            ]));

        using var rectangleStream = new MemoryStream(rectangleGuide);
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
            rectangleStream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            decoder.Frames[0],
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            0);
        var renderedPixels = new byte[stride * height];
        converted.CopyPixels(renderedPixels, stride, 0);
        var outsideOffset = 5 * stride + 5 * 4;
        var insideOffset = 40 * stride + 50 * 4;

        Assert(converted.PixelWidth == width && converted.PixelHeight == height, "region guide retains the source dimensions");
        Assert(
            renderedPixels[outsideOffset] == sourcePixels[outsideOffset] &&
            renderedPixels[outsideOffset + 2] == sourcePixels[outsideOffset + 2],
            "region guide preserves pixels outside the marked area");
        Assert(
            renderedPixels[insideOffset + 2] > renderedPixels[outsideOffset + 2],
            "rectangle region is visibly marked in red");
        Assert(
            !rectangleGuide.SequenceEqual(freehandGuide),
            "freehand and rectangle selections produce distinct guides");

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"synthiacode-image-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var guidePath = Path.Combine(tempDirectory, "region-guide.png");
        File.WriteAllBytes(guidePath, rectangleGuide);
        try
        {
            var editor = new GeneratedImageEditWindow(guidePath);
            Assert(editor.EditorImage.Source is not null, "the edit window loads the generated image");
            Assert(editor.Selection is null, "the editor begins without an implicit region selection");
            Assert(editor.Title.Contains("region-guide.png", StringComparison.Ordinal), "the editor identifies its source image");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    });

    private static Task GeneratedImageEditorAdjustsDrawBrushSizeAsync() => RunOnStaAsync(() =>
    {
        const int width = 100;
        const int height = 80;
        const int stride = width * 4;
        var sourcePixels = new byte[stride * height];
        for (var index = 0; index < sourcePixels.Length; index += 4)
        {
            sourcePixels[index] = 180;
            sourcePixels[index + 1] = 40;
            sourcePixels[index + 2] = 20;
            sourcePixels[index + 3] = 255;
        }

        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            sourcePixels,
            stride);
        source.Freeze();
        NormalizedImagePoint[] freehandPoints =
        [
            new(0.2, 0.5),
            new(0.5, 0.5),
            new(0.8, 0.5)
        ];

        var minimumGuide = GeneratedImageRegionGuideRenderer.RenderPng(
            source,
            GeneratedImageEditRegion.Freehand(freehandPoints, 4));
        var maximumGuide = GeneratedImageRegionGuideRenderer.RenderPng(
            source,
            GeneratedImageEditRegion.Freehand(freehandPoints, 64));
        Assert(
            CountChangedPixels(maximumGuide, sourcePixels, stride) >
            CountChangedPixels(minimumGuide, sourcePixels, stride) * 2,
            "the selected brush size changes the width of the exported freehand guide");

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"synthiacode-brush-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "brush-size-source.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using (var stream = File.Create(imagePath))
        {
            encoder.Save(stream);
        }

        try
        {
            var editor = new GeneratedImageEditWindow(imagePath);
            var buttons = Descendants<Button>((DependencyObject)editor.Content).ToArray();
            var drawButton = buttons.Single(
                button => AutomationProperties.GetName(button) == "Draw over the area to change");
            var decreaseButton = buttons.Single(
                button => AutomationProperties.GetName(button) == "Decrease draw brush size");
            var increaseButton = buttons.Single(
                button => AutomationProperties.GetName(button) == "Increase draw brush size");
            var brushSizeStatus = Descendants<TextBlock>((DependencyObject)editor.Content).Single(
                text => AutomationProperties.GetName(text) == "Draw brush size");

            Assert(!decreaseButton.IsEnabled && !increaseButton.IsEnabled, "brush controls are inactive for the rectangle tool");
            drawButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(decreaseButton.IsEnabled && increaseButton.IsEnabled, "selecting Draw enables both brush controls");
            Assert(editor.BrushSize == 18, "the draw brush starts at the existing visual width");

            increaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(editor.BrushSize == 22, "increase advances the draw brush by one bounded step");
            Assert(brushSizeStatus.Text.Contains("22 px", StringComparison.Ordinal), "the current brush size is visible");
            decreaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(editor.BrushSize == 18, "decrease restores the previous draw brush size");

            for (var index = 0; index < 20; index++)
            {
                decreaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert(editor.BrushSize == 4 && !decreaseButton.IsEnabled, "decrease stops at the minimum brush size");

            for (var index = 0; index < 20; index++)
            {
                increaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert(editor.BrushSize == 64 && !increaseButton.IsEnabled, "increase stops at the maximum brush size");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    });

    private static Task RendererPreservesUnsafeAndMalformedLinksAsync() => RunOnStaAsync(() =>
    {
        const string source = "[local](file:///C:/secret.txt) [script](javascript:alert(1)) [redirect](javascript:https://example.com) [unfinished](https://example.com";
        var renderer = new MarkdownTextBlock { Markdown = source };

        Assert(!renderer.Inlines.OfType<Hyperlink>().Any(), "unsupported or malformed destinations are not links");
        Assert(InlineText(renderer) == source, "unsupported or malformed source remains visible verbatim");
    });

    private static Task RendererRoutesLinkActivationAsync() => RunOnStaAsync(() =>
    {
        Uri? activatedUri = null;
        var renderer = new MarkdownTextBlock
        {
            Markdown = "[Open docs](https://example.com/docs)",
            LinkCommand = new RelayCommand(parameter => activatedUri = parameter as Uri)
        };

        var link = renderer.Inlines.OfType<Hyperlink>().Single();
        link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri!, null));

        Assert(activatedUri?.AbsoluteUri == "https://example.com/docs", "link activation receives the validated URI");
    });

    private static Task ExternalLinkPolicyPermitsOnlyWebDestinations()
    {
        Assert(ExternalUriPolicy.IsSupported(new Uri("https://example.com")), "https is supported");
        Assert(ExternalUriPolicy.IsSupported(new Uri("http://example.com")), "http is supported");
        Assert(!ExternalUriPolicy.IsSupported(new Uri("file:///C:/secret.txt")), "file URIs are rejected");
        Assert(!ExternalUriPolicy.IsSupported(new Uri("javascript:alert(1)")), "script URIs are rejected");
        return Task.CompletedTask;
    }

    private static string InlineText(TextBlock block) => string.Concat(block.Inlines.Select(InlineText));

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        Span span => string.Concat(span.Inlines.Select(InlineText)),
        _ => string.Empty
    };

    private static int CountChangedPixels(byte[] png, byte[] sourcePixels, int stride)
    {
        using var stream = new MemoryStream(png);
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            decoder.Frames[0],
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            0);
        var renderedPixels = new byte[sourcePixels.Length];
        converted.CopyPixels(renderedPixels, stride, 0);

        var changed = 0;
        for (var index = 0; index < renderedPixels.Length; index += 4)
        {
            if (renderedPixels[index] != sourcePixels[index] ||
                renderedPixels[index + 1] != sourcePixels[index + 1] ||
                renderedPixels[index + 2] != sourcePixels[index + 2])
            {
                changed++;
            }
        }

        return changed;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
