using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using SynthiaCode.App.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;

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
        var codeScroller = (ScrollViewer)codeBlock.Child;
        var codeText = (TextBlock)codeScroller.Content;
        Assert(codeScroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "fenced code supports horizontal scrolling");
        Assert(codeText.Text.Contains("**literal markdown**", StringComparison.Ordinal), "fenced code preserves markdown syntax literally");
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
