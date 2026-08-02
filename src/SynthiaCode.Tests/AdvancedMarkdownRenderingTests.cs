using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using SynthiaCode.App.Controls;

internal static class AdvancedMarkdownRenderingTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("markdown renderer embeds safe remote images", RendererEmbedsSafeRemoteImagesAsync),
        ("markdown renderer supports safe raw HTML", RendererSupportsSafeRawHtmlAsync),
        ("markdown renderer lays out nested lists", RendererLaysOutNestedListsAsync),
        ("markdown renderer links and appends footnotes", RendererLinksAndAppendsFootnotesAsync),
        ("markdown renderer lays out definition lists", RendererLaysOutDefinitionListsAsync),
        ("markdown renderer highlights fenced code by language", RendererHighlightsFencedCodeAsync),
        ("markdown fenced code exposes an exact copy action", RendererCopiesIndividualCodeBlockAsync)
    ];

    private static Task RendererEmbedsSafeRemoteImagesAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = "Architecture:\n\n![service topology](https://cdn.example.com/architecture.png)"
        };

        var preview = renderer.Inlines
            .OfType<InlineUIContainer>()
            .Select(container => container.Child)
            .Single(child => AutomationProperties.GetName(child) == "Remote image preview: service topology");
        var image = Descendants<Image>(preview).Single();
        var bitmap = image.Source as BitmapImage;

        Assert(bitmap?.UriSource?.AbsoluteUri == "https://cdn.example.com/architecture.png", "remote image retains its validated HTTPS source");
        Assert(image.MaxWidth > 0 && image.MaxHeight > 0 && image.Stretch == System.Windows.Media.Stretch.Uniform, "remote image is bounded and preserves aspect ratio");
        Assert(Descendants<Hyperlink>(preview).Single().NavigateUri?.AbsoluteUri == bitmap!.UriSource.AbsoluteUri, "remote image card keeps a safe clickable source");

        const string unsafeSource = "![tracking](data:image/png;base64,AAAA)";
        var unsafeRenderer = new MarkdownTextBlock { Markdown = unsafeSource };
        Assert(!unsafeRenderer.Inlines.OfType<InlineUIContainer>().Any(), "unsupported image schemes never create a remote image");
        Assert(InlineText(unsafeRenderer) == unsafeSource, "unsupported remote image source remains literal");
    });

    private static Task RendererSupportsSafeRawHtmlAsync() => RunOnStaAsync(() =>
    {
        const string source = "Use <strong>safe</strong>, <em>careful</em>, and <code>value</code>.<br>Next <!--hidden--> <a href=\"https://example.com/html\">reference</a>. <script>alert('x')</script>";
        var renderer = new MarkdownTextBlock { Markdown = source };

        Assert(renderer.Inlines.OfType<Bold>().Any(bold => InlineText(bold) == "safe"), "strong HTML maps to native bold text");
        Assert(renderer.Inlines.OfType<Italic>().Any(italic => InlineText(italic) == "careful"), "emphasis HTML maps to native italic text");
        Assert(
            renderer.Inlines.OfType<Run>().Any(run => AutomationProperties.GetName(run) == "Inline HTML code" && run.Text == "value"),
            "HTML code maps to styled literal code");
        Assert(renderer.Inlines.OfType<LineBreak>().Any(), "HTML break maps to a native line break");
        Assert(
            renderer.Inlines.OfType<Hyperlink>().Single().NavigateUri?.AbsoluteUri == "https://example.com/html",
            "safe HTML anchors reuse the external-link policy");

        var renderedText = InlineText(renderer);
        Assert(!renderedText.Contains("hidden", StringComparison.Ordinal), "HTML comments are not displayed");
        Assert(renderedText.Contains("<script>alert('x')</script>", StringComparison.Ordinal), "executable HTML stays visible and inert");
    });

    private static Task RendererLaysOutNestedListsAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = """
                - Parent
                  - Child with **formatting**
                    1. Grandchild
                  - [x] Nested task
                - Sibling
                """
        };

        var list = renderer.Inlines
            .OfType<InlineUIContainer>()
            .Select(container => container.Child)
            .Single(child => AutomationProperties.GetName(child) == "Markdown list");
        var rows = Descendants<FrameworkElement>(list)
            .Where(element => AutomationProperties.GetName(element).StartsWith("Markdown list item depth ", StringComparison.Ordinal))
            .ToArray();

        Assert(rows.Length == 5, "nested list keeps every item in one hierarchy");
        Assert(rows.Select(row => row.Margin.Left).SequenceEqual([0d, 24d, 48d, 24d, 0d]), "list depth is represented by real layout margins");
        Assert(Descendants<Bold>(list).Any(bold => InlineText(bold) == "formatting"), "inline Markdown remains active in nested items");
        Assert(
            rows.Any(row => AutomationProperties.GetHelpText(row) == "Checked task"),
            "nested task-list state remains accessible");
    });

    private static Task RendererLinksAndAppendsFootnotesAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = """
                Choose the stable API[^stability] and keep missing[^unknown] literal.

                [^stability]: Stable APIs preserve compatibility across minor releases.
                """
        };

        var reference = renderer.Inlines
            .OfType<Hyperlink>()
            .Single(link => AutomationProperties.GetName(link) == "Markdown footnote reference: stability");
        Assert(reference.BaselineAlignment == BaselineAlignment.Superscript, "footnote references render as superscript navigation");
        Assert(InlineText(reference) == "1", "footnote references use display ordinals");

        var footnotes = renderer.Inlines
            .OfType<InlineUIContainer>()
            .Select(container => container.Child)
            .Single(child => AutomationProperties.GetName(child) == "Markdown footnotes");
        Assert(
            Descendants<MarkdownTextBlock>(footnotes).Any(block => InlineText(block).Contains("Stable APIs preserve compatibility", StringComparison.Ordinal)),
            "referenced definitions move to the footnote section");
        Assert(!InlineText(renderer).Contains("[^stability]:", StringComparison.Ordinal), "footnote declaration is removed from its source position");
        Assert(InlineText(renderer).Contains("[^unknown]", StringComparison.Ordinal), "missing footnote references remain literal");

        var fenced = new MarkdownTextBlock { Markdown = "```\n[^sample]: literal code\n```" };
        var fencedCode = fenced.Inlines
            .OfType<InlineUIContainer>()
            .SelectMany(container => Descendants<TextBlock>(container.Child))
            .Single(block => AutomationProperties.GetName(block) == "Highlighted Code code");
        Assert(InlineText(fencedCode) == "[^sample]: literal code", "footnote-like source inside a fence remains literal code");
    });

    private static Task RendererLaysOutDefinitionListsAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = """
                Renderer
                : Converts **Markdown** into native WPF content.
                : Preserves safe interaction.

                Fallback
                : Keeps malformed input visible.
                """
        };

        var definitions = renderer.Inlines
            .OfType<InlineUIContainer>()
            .Select(container => container.Child)
            .Single(child => AutomationProperties.GetName(child) == "Markdown definition list");
        var terms = Descendants<MarkdownTextBlock>(definitions)
            .Where(block => AutomationProperties.GetName(block).StartsWith("Markdown definition term:", StringComparison.Ordinal))
            .ToArray();
        var descriptions = Descendants<MarkdownTextBlock>(definitions)
            .Where(block => AutomationProperties.GetName(block) == "Markdown definition")
            .ToArray();

        Assert(terms.Length == 2 && descriptions.Length == 3, "definition list groups multiple terms and descriptions");
        Assert(terms.All(term => term.FontWeight == FontWeights.SemiBold), "definition terms are visually distinguished");
        Assert(descriptions.All(description => description.Margin.Left == 24), "definitions use a real nested layout margin");
        Assert(descriptions.SelectMany(description => description.Inlines).OfType<Bold>().Any(), "inline Markdown remains active inside definitions");
    });

    private static Task RendererHighlightsFencedCodeAsync() => RunOnStaAsync(() =>
    {
        var renderer = new MarkdownTextBlock
        {
            Markdown = "```csharp\n// result\nvar total = 42;\nConsole.WriteLine(\"done\");\n```"
        };

        var codeBlock = renderer.Inlines
            .OfType<InlineUIContainer>()
            .Select(container => container.Child)
            .Single(child => AutomationProperties.GetName(child) == "Markdown fenced code block");
        var code = Descendants<TextBlock>(codeBlock)
            .Single(block => AutomationProperties.GetName(block) == "Highlighted C# code");

        Assert(InlineText(code) == "// result\nvar total = 42;\nConsole.WriteLine(\"done\");", "syntax highlighting preserves exact code text");
        Assert(code.Inlines.OfType<Run>().Any(run => AutomationProperties.GetName(run) == "Syntax comment"), "comments receive a syntax token");
        Assert(code.Inlines.OfType<Run>().Any(run => AutomationProperties.GetName(run) == "Syntax keyword" && run.Text == "var"), "language keywords receive a syntax token");
        Assert(code.Inlines.OfType<Run>().Any(run => AutomationProperties.GetName(run) == "Syntax number" && run.Text == "42"), "numbers receive a syntax token");
        Assert(code.Inlines.OfType<Run>().Any(run => AutomationProperties.GetName(run) == "Syntax string" && run.Text == "\"done\""), "strings receive a syntax token");
        Assert(
            Descendants<TextBlock>(codeBlock).Any(block => AutomationProperties.GetName(block) == "Code language: C#" && block.Text == "C#"),
            "normalized language label is shown in the code-block header");
    });

    private static Task RendererCopiesIndividualCodeBlockAsync() => RunOnStaAsync(() =>
    {
        const string expected = "var total = 42;\nConsole.WriteLine(total);";
        var renderer = new MarkdownTextBlock
        {
            Markdown = $"```csharp\n{expected}\n```"
        };
        var codeBlock = renderer.Inlines
            .OfType<InlineUIContainer>()
            .Select(container => container.Child)
            .Single(child => AutomationProperties.GetName(child) == "Markdown fenced code block");
        var copy = Descendants<Button>(codeBlock)
            .Single(button => AutomationProperties.GetName(button) == "Copy C# code");

        Clipboard.SetText("clipboard sentinel");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert(Clipboard.GetText() == expected, "code-block copy writes only that block's exact source");

        var trailingBlankLine = new MarkdownTextBlock { Markdown = "```text\nline\n\n```" };
        var trailingCopy = trailingBlankLine.Inlines
            .OfType<InlineUIContainer>()
            .SelectMany(container => Descendants<Button>(container.Child))
            .Single(button => AutomationProperties.GetName(button) == "Copy text code");
        trailingCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(Clipboard.GetText() == "line\n", "code-block copy preserves an intentional trailing blank line");
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in Children(root))
        {
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

    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        switch (parent)
        {
            case TextBlock textBlock:
                foreach (var inline in textBlock.Inlines)
                {
                    yield return inline;
                }
                break;
            case Span span:
                foreach (var inline in span.Inlines)
                {
                    yield return inline;
                }
                break;
            case InlineUIContainer container when container.Child is not null:
                yield return container.Child;
                break;
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    yield return child;
                }
                break;
            case Border border when border.Child is not null:
                yield return border.Child;
                break;
            case ScrollViewer scroller when scroller.Content is DependencyObject content:
                yield return content;
                break;
            case ContentControl contentControl when contentControl.Content is DependencyObject content:
                yield return content;
                break;
        }
    }

    private static string InlineText(TextBlock block) => string.Concat(block.Inlines.Select(InlineText));

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
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
