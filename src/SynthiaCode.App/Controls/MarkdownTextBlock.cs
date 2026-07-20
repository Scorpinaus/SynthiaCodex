using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using SynthiaCode.App.Services;

namespace SynthiaCode.App.Controls;

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
        var segmentStart = 0;
        for (var lineStart = 0; lineStart < source.Length; lineStart = FindNextLineStart(source, lineStart))
        {
            if (!TryReadMarkdownTable(source, lineStart, out var tableEnd, out var table))
            {
                continue;
            }

            AppendInlineMarkdown(Inlines, source[segmentStart..lineStart]);
            Inlines.Add(new InlineUIContainer(CreateTable(table)));
            segmentStart = tableEnd;
            lineStart = tableEnd;
        }

        AppendInlineMarkdown(Inlines, source[segmentStart..]);
    }

    private void AppendInlineMarkdown(InlineCollection inlines, string source)
    {
        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '`' && TryReadCodeSpan(source, index, out var codeEnd))
            {
                inlines.Add(new Run(source[index..codeEnd]));
                index = codeEnd;
                continue;
            }

            if (IsStrongMarkerStart(source, index) &&
                TryReadStrong(source, index, out var strongEnd, out var strongContent))
            {
                var strong = new Bold();
                AppendInlineMarkdown(strong.Inlines, strongContent);
                inlines.Add(strong);
                index = strongEnd;
                continue;
            }

            if (source[index] == '[' && TryReadMarkdownLink(source, index, out var linkEnd, out var label, out var target))
            {
                AddLink(inlines, label, target);
                index = linkEnd;
                continue;
            }

            if (source[index] == '[' && TryReadUnfinishedMarkdownLink(source, index, out var unfinishedEnd))
            {
                inlines.Add(new Run(source[index..unfinishedEnd]));
                index = unfinishedEnd;
                continue;
            }

            if (source[index] == '<' && TryReadAutolink(source, index, out var autolinkEnd, out var autolink))
            {
                AddLink(inlines, autolink.AbsoluteUri, autolink);
                index = autolinkEnd;
                continue;
            }

            if (IsBareUrlStart(source, index) && TryReadBareUrl(source, index, out var urlEnd, out var bareUrl))
            {
                AddLink(inlines, bareUrl.AbsoluteUri, bareUrl);
                index = urlEnd;
                continue;
            }

            var plainEnd = FindNextCandidate(source, index + 1);
            inlines.Add(new Run(source[index..plainEnd]));
            index = plainEnd;
        }
    }

    private Grid CreateTable(MarkdownTable table)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };
        grid.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ActualWidth)) { Source = this });

        for (var column = 0; column < table.Header.Length; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rows = new List<string[]> { table.Header };
        rows.AddRange(table.Rows);
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < table.Header.Length; column++)
            {
                var cellText = new MarkdownTextBlock
                {
                    Markdown = rows[row][column],
                    LinkCommand = LinkCommand,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = FontFamily,
                    FontSize = FontSize,
                    FontStyle = FontStyle,
                    FontStretch = FontStretch,
                    FontWeight = row == 0 ? FontWeights.SemiBold : FontWeight,
                    Foreground = Foreground
                };
                if (!double.IsNaN(LineHeight))
                {
                    cellText.LineHeight = LineHeight;
                }

                var cell = new Border
                {
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(7, 5, 7, 5),
                    Child = cellText
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
                if (row == 0)
                {
                    cell.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
                }

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private void AddLink(InlineCollection inlines, string label, Uri target)
    {
        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = target,
            ToolTip = new ToolTip { Content = target.AbsoluteUri }
        };
        link.RequestNavigate += OnLinkRequestNavigate;
        inlines.Add(link);
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

    private static bool IsStrongMarkerStart(string source, int start) =>
        start + 1 < source.Length &&
        ((source[start] == '*' && source[start + 1] == '*') ||
         (source[start] == '_' && source[start + 1] == '_'));

    private static bool TryReadStrong(string source, int start, out int end, out string content)
    {
        end = start;
        content = string.Empty;
        if (!IsStrongMarkerStart(source, start))
        {
            return false;
        }

        var marker = source.Substring(start, 2);
        var closing = source.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (closing <= start + marker.Length)
        {
            return false;
        }

        content = source[(start + marker.Length)..closing];
        end = closing + marker.Length;
        return true;
    }

    private static bool TryReadMarkdownTable(string source, int start, out int end, out MarkdownTable table)
    {
        end = start;
        table = null!;
        if (start > 0 && source[start - 1] != '\n')
        {
            return false;
        }

        ReadLine(source, start, out var headerLine, out _, out var delimiterStart);
        if (!TryParseTableRow(headerLine, out var header) || delimiterStart >= source.Length)
        {
            return false;
        }

        ReadLine(source, delimiterStart, out var delimiterLine, out var delimiterEnd, out var nextRowStart);
        if (!TryParseTableRow(delimiterLine, out var delimiter) ||
            delimiter.Length != header.Length ||
            !IsDelimiterRow(delimiter))
        {
            return false;
        }

        var rows = new List<string[]>();
        end = delimiterEnd;
        while (nextRowStart < source.Length)
        {
            ReadLine(source, nextRowStart, out var rowLine, out var rowEnd, out var followingRowStart);
            if (!TryParseTableRow(rowLine, out var row) ||
                row.Length != header.Length ||
                IsDelimiterRow(row))
            {
                break;
            }

            rows.Add(row);
            end = rowEnd;
            nextRowStart = followingRowStart;
        }

        table = new MarkdownTable(header, rows);
        return true;
    }

    private static bool TryParseTableRow(string line, out string[] cells)
    {
        cells = [];
        var row = line.Trim();
        var hasLeadingPipe = row.StartsWith('|');
        var hasTrailingPipe = row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal);
        if (!hasLeadingPipe && !hasTrailingPipe && !row.Contains('|'))
        {
            return false;
        }

        if (hasLeadingPipe)
        {
            row = row[1..];
        }
        if (hasTrailingPipe && row.Length > 0)
        {
            row = row[..^1];
        }

        var parsed = new List<string>();
        var cell = new StringBuilder();
        var isInCodeSpan = false;
        for (var index = 0; index < row.Length; index++)
        {
            if (row[index] == '\\' && index + 1 < row.Length && row[index + 1] == '|')
            {
                cell.Append('|');
                index++;
                continue;
            }

            if (row[index] == '`')
            {
                isInCodeSpan = !isInCodeSpan;
            }
            if (row[index] == '|' && !isInCodeSpan)
            {
                parsed.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(row[index]);
        }
        parsed.Add(cell.ToString().Trim());
        cells = [.. parsed];
        return true;
    }

    private static bool IsDelimiterRow(IEnumerable<string> cells) => cells.All(cell =>
    {
        var marker = cell.Trim();
        if (marker.StartsWith(':'))
        {
            marker = marker[1..];
        }
        if (marker.EndsWith(':'))
        {
            marker = marker[..^1];
        }

        return marker.Length >= 3 && marker.All(character => character == '-');
    });

    private static void ReadLine(
        string source,
        int start,
        out string line,
        out int lineEnd,
        out int nextLineStart)
    {
        lineEnd = start;
        while (lineEnd < source.Length && source[lineEnd] is not '\r' and not '\n')
        {
            lineEnd++;
        }

        line = source[start..lineEnd];
        nextLineStart = lineEnd;
        if (nextLineStart < source.Length && source[nextLineStart] == '\r')
        {
            nextLineStart++;
        }
        if (nextLineStart < source.Length && source[nextLineStart] == '\n')
        {
            nextLineStart++;
        }
    }

    private static int FindNextLineStart(string source, int start)
    {
        var newline = source.IndexOf('\n', start);
        return newline < 0 ? source.Length : newline + 1;
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
            if (source[index] is '[' or '<' or '`' ||
                IsStrongMarkerStart(source, index) ||
                IsBareUrlStart(source, index))
            {
                return index;
            }
        }

        return source.Length;
    }

    private sealed record MarkdownTable(string[] Header, IReadOnlyList<string[]> Rows);
}
