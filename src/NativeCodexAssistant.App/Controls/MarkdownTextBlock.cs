using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using NativeCodexAssistant.App.Services;

namespace NativeCodexAssistant.App.Controls;

public sealed class MarkdownTextBlock : TextBlock
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(string.Empty, OnContentPropertyChanged));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand),
        typeof(ICommand),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(null, OnContentPropertyChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    private static void OnContentPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownTextBlock)dependencyObject).RenderMarkdown();

    private void RenderMarkdown()
    {
        Inlines.Clear();

        var source = Markdown ?? string.Empty;
        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '`' && TryReadCodeSpan(source, index, out var codeEnd))
            {
                Inlines.Add(new Run(source[index..codeEnd]));
                index = codeEnd;
                continue;
            }

            if (source[index] == '[' && TryReadMarkdownLink(source, index, out var linkEnd, out var label, out var target))
            {
                AddLink(label, target);
                index = linkEnd;
                continue;
            }

            if (source[index] == '[' && TryReadUnfinishedMarkdownLink(source, index, out var unfinishedEnd))
            {
                Inlines.Add(new Run(source[index..unfinishedEnd]));
                index = unfinishedEnd;
                continue;
            }

            if (source[index] == '<' && TryReadAutolink(source, index, out var autolinkEnd, out var autolink))
            {
                AddLink(autolink.AbsoluteUri, autolink);
                index = autolinkEnd;
                continue;
            }

            if (IsBareUrlStart(source, index) && TryReadBareUrl(source, index, out var urlEnd, out var bareUrl))
            {
                AddLink(bareUrl.AbsoluteUri, bareUrl);
                index = urlEnd;
                continue;
            }

            var plainEnd = FindNextCandidate(source, index + 1);
            Inlines.Add(new Run(source[index..plainEnd]));
            index = plainEnd;
        }
    }

    private void AddLink(string label, Uri target)
    {
        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = target,
            ToolTip = target.AbsoluteUri
        };
        link.RequestNavigate += OnLinkRequestNavigate;
        Inlines.Add(link);
    }

    private void OnLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (LinkCommand?.CanExecute(e.Uri) == true)
        {
            LinkCommand.Execute(e.Uri);
        }

        e.Handled = true;
    }

    private static bool TryReadCodeSpan(string source, int start, out int end)
    {
        var closing = source.IndexOf('`', start + 1);
        if (closing < 0)
        {
            end = start;
            return false;
        }

        end = closing + 1;
        return true;
    }

    private static bool TryReadMarkdownLink(string source, int start, out int end, out string label, out Uri target)
    {
        end = start;
        label = string.Empty;
        target = null!;

        var labelEnd = source.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd <= start + 1)
        {
            return false;
        }

        var targetEnd = source.IndexOf(')', labelEnd + 2);
        if (targetEnd < 0)
        {
            return false;
        }

        var destination = source[(labelEnd + 2)..targetEnd];
        if (!TryGetSafeUri(destination, out target))
        {
            return false;
        }

        label = source[(start + 1)..labelEnd];
        end = targetEnd + 1;
        return true;
    }

    private static bool TryReadUnfinishedMarkdownLink(string source, int start, out int end)
    {
        end = start;
        var labelEnd = source.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd < 0 || source.IndexOf(')', labelEnd + 2) >= 0)
        {
            return false;
        }

        end = source.Length;
        return true;
    }

    private static bool TryReadAutolink(string source, int start, out int end, out Uri target)
    {
        end = start;
        target = null!;
        var closing = source.IndexOf('>', start + 1);
        if (closing < 0 || !TryGetSafeUri(source[(start + 1)..closing], out target))
        {
            return false;
        }

        end = closing + 1;
        return true;
    }

    private static bool IsBareUrlStart(string source, int start)
    {
        if (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] is '_' or '-'))
        {
            return false;
        }

        return source.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               source.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBareUrl(string source, int start, out int end, out Uri target)
    {
        end = start;
        target = null!;
        var candidateEnd = start;
        while (candidateEnd < source.Length && !char.IsWhiteSpace(source[candidateEnd]) && source[candidateEnd] is not '<' and not '>')
        {
            candidateEnd++;
        }

        while (candidateEnd > start && source[candidateEnd - 1] is '.' or ',' or ';' or ':' or '!' or '?')
        {
            candidateEnd--;
        }

        if (candidateEnd == start || !TryGetSafeUri(source[start..candidateEnd], out target))
        {
            return false;
        }

        end = candidateEnd;
        return true;
    }

    private static bool TryGetSafeUri(string value, out Uri uri) =>
        ExternalUriPolicy.TryCreateSupportedUri(value, out uri);

    private static int FindNextCandidate(string source, int start)
    {
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] is '[' or '<' or '`' || IsBareUrlStart(source, index))
            {
                return index;
            }
        }

        return source.Length;
    }
}
