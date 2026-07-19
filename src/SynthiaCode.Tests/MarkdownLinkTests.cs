using System.Windows;
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
        ("markdown link renderer recognizes safe assistant links", RendererRecognizesSafeLinksAsync),
        ("markdown link renderer preserves unsafe and malformed links", RendererPreservesUnsafeAndMalformedLinksAsync),
        ("markdown link renderer routes link activation through its command", RendererRoutesLinkActivationAsync),
        ("external link policy permits only web destinations", ExternalLinkPolicyPermitsOnlyWebDestinations)
    ];

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
        const string source = "[local](file:///C:/secret.txt) [script](javascript:alert(1)) [redirect](javascript:https://example.com) `https://example.com/code` [unfinished](https://example.com";
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
