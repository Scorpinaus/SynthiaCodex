using System.Text;
using System.Windows;
using System.Windows.Automation;
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
            Inline? block = null;
            var blockEnd = lineStart;
            if (TryReadFencedCodeBlock(source, lineStart, out blockEnd, out var code))
            {
                block = new InlineUIContainer(CreateFencedCodeBlock(code));
            }
            else if (TryReadMarkdownTable(source, lineStart, out blockEnd, out var table))
            {
                block = new InlineUIContainer(CreateTable(table));
            }
            else if (TryReadBlockQuote(source, lineStart, out blockEnd, out var quote))
            {
                block = new InlineUIContainer(CreateBlockQuote(quote));
            }
            else if (TryReadHeading(source, lineStart, out blockEnd, out var headingLevel, out var headingContent))
            {
                block = CreateHeading(headingLevel, headingContent);
            }
            else if (TryReadHorizontalRule(source, lineStart, out blockEnd))
            {
                block = new InlineUIContainer(CreateHorizontalRule());
            }
            else if (TryReadListItem(source, lineStart, out blockEnd, out var listPrefix, out var listContent, out var listAutomationName))
            {
                block = CreateListItem(listPrefix, listContent, listAutomationName);
            }

            if (block is null)
            {
                continue;
            }

            AppendInlineMarkdown(Inlines, source[segmentStart..lineStart]);
            Inlines.Add(block);
            segmentStart = blockEnd;
            lineStart = blockEnd;
        }

        AppendInlineMarkdown(Inlines, source[segmentStart..]);
    }

    private void AppendInlineMarkdown(InlineCollection inlines, string source)
    {
        for (var index = 0; index < source.Length;)
        {
            if (IsEscapedPunctuation(source, index))
            {
                inlines.Add(new Run(source[(index + 1)..(index + 2)]));
                index += 2;
                continue;
            }

            if (source[index] == '`' && index + 1 < source.Length && source[index + 1] == '`')
            {
                var markerEnd = index + 2;
                while (markerEnd < source.Length && source[markerEnd] == '`')
                {
                    markerEnd++;
                }

                inlines.Add(new Run(source[index..markerEnd]));
                index = markerEnd;
                continue;
            }

            if (source[index] == '`' && TryReadCodeSpan(source, index, out var codeEnd))
            {
                var code = new Run(source[(index + 1)..(codeEnd - 1)]);
                code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                code.SetResourceReference(TextElement.BackgroundProperty, "SubtleBrush");
                AutomationProperties.SetName(code, "Inline code");
                inlines.Add(code);
                index = codeEnd;
                continue;
            }

            if (IsCombinedEmphasisMarkerStart(source, index) &&
                TryReadDelimited(source, index, source.Substring(index, 3), out var combinedEnd, out var combinedContent))
            {
                var combined = new Bold();
                AutomationProperties.SetName(combined, "Bold italic");
                var emphasis = new Italic();
                AppendInlineMarkdown(emphasis.Inlines, combinedContent);
                combined.Inlines.Add(emphasis);
                inlines.Add(combined);
                index = combinedEnd;
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

            if (IsStrikethroughMarkerStart(source, index) &&
                TryReadDelimited(source, index, "~~", out var strikeEnd, out var strikeContent))
            {
                var strike = new Span { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                AutomationProperties.SetName(strike, "Strikethrough");
                AppendInlineMarkdown(strike.Inlines, strikeContent);
                inlines.Add(strike);
                index = strikeEnd;
                continue;
            }

            if (IsEmphasisMarkerStart(source, index) &&
                TryReadDelimited(source, index, source[index].ToString(), out var emphasisEnd, out var emphasisContent))
            {
                var emphasis = new Italic();
                AppendInlineMarkdown(emphasis.Inlines, emphasisContent);
                inlines.Add(emphasis);
                index = emphasisEnd;
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

    private Span CreateHeading(int level, string content)
    {
        var heading = new Span
        {
            FontSize = FontSize * (level switch
            {
                1 => 1.6,
                2 => 1.45,
                3 => 1.3,
                4 => 1.2,
                5 => 1.1,
                _ => 1.05
            }),
            FontWeight = FontWeights.Bold
        };
        AutomationProperties.SetName(heading, $"Markdown heading level {level}");
        AppendInlineMarkdown(heading.Inlines, content);
        return heading;
    }

    private Span CreateListItem(string prefix, string content, string automationName)
    {
        var item = new Span();
        AutomationProperties.SetName(item, automationName);
        item.Inlines.Add(new Run(prefix));
        AppendInlineMarkdown(item.Inlines, content);
        return item;
    }

    private Border CreateHorizontalRule()
    {
        var rule = new Border
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 8)
        };
        rule.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        BindBlockWidth(rule);
        AutomationProperties.SetName(rule, "Markdown horizontal rule");
        return rule;
    }

    private Border CreateBlockQuote(string content)
    {
        var quote = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(10, 4, 8, 4),
            Child = CreateNestedMarkdownTextBlock(content)
        };
        quote.SetResourceReference(Border.BorderBrushProperty, "SignalBrush");
        BindBlockWidth(quote);
        AutomationProperties.SetName(quote, "Markdown block quote");
        return quote;
    }

    private Border CreateFencedCodeBlock(string content)
    {
        var code = new TextBlock
        {
            Text = content.TrimEnd('\r', '\n'),
            TextWrapping = TextWrapping.NoWrap,
            FontSize = FontSize,
            Foreground = Foreground
        };
        code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");

        var scroller = new ScrollViewer
        {
            Content = code,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var block = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(9, 7, 9, 7),
            Child = scroller
        };
        block.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
        block.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        BindBlockWidth(block);
        AutomationProperties.SetName(block, "Markdown fenced code block");
        return block;
    }

    private MarkdownTextBlock CreateNestedMarkdownTextBlock(string content)
    {
        var nested = new MarkdownTextBlock
        {
            Markdown = content,
            LinkCommand = LinkCommand,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontStyle = FontStyle,
            FontStretch = FontStretch,
            FontWeight = FontWeight,
            Foreground = Foreground
        };
        if (!double.IsNaN(LineHeight))
        {
            nested.LineHeight = LineHeight;
        }

        return nested;
    }

    private void BindBlockWidth(FrameworkElement block) =>
        block.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ActualWidth)) { Source = this });

    private Grid CreateTable(MarkdownTable table)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };
        BindBlockWidth(grid);
        AutomationProperties.SetName(grid, "Markdown table");

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
                var cellText = CreateNestedMarkdownTextBlock(rows[row][column]);
                cellText.FontWeight = row == 0 ? FontWeights.SemiBold : FontWeight;
                cellText.TextAlignment = table.Alignments[column] switch
                {
                    MarkdownTableAlignment.Center => TextAlignment.Center,
                    MarkdownTableAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                };

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

    private static bool IsCombinedEmphasisMarkerStart(string source, int start) =>
        start + 2 < source.Length &&
        ((source[start] == '*' && source[start + 1] == '*' && source[start + 2] == '*') ||
         (source[start] == '_' && source[start + 1] == '_' && source[start + 2] == '_'));

    private static bool IsStrikethroughMarkerStart(string source, int start) =>
        start + 1 < source.Length && source[start] == '~' && source[start + 1] == '~';

    private static bool IsEmphasisMarkerStart(string source, int start)
    {
        if (start >= source.Length || source[start] is not '*' and not '_' || IsStrongMarkerStart(source, start))
        {
            return false;
        }

        return start == 0 || !char.IsLetterOrDigit(source[start - 1]);
    }

    private static bool IsEscapedPunctuation(string source, int start) =>
        start + 1 < source.Length &&
        source[start] == '\\' &&
        source[start + 1] is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or
            '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|' or '>' or '~';

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

    private static bool TryReadDelimited(
        string source,
        int start,
        string marker,
        out int end,
        out string content)
    {
        end = start;
        content = string.Empty;
        var searchStart = start + marker.Length;
        for (var closing = source.IndexOf(marker, searchStart, StringComparison.Ordinal);
             closing >= 0;
             closing = source.IndexOf(marker, closing + marker.Length, StringComparison.Ordinal))
        {
            if (closing == searchStart || (closing > 0 && source[closing - 1] == '\\'))
            {
                continue;
            }

            content = source[searchStart..closing];
            end = closing + marker.Length;
            return true;
        }

        return false;
    }

    private static bool TryReadHeading(
        string source,
        int start,
        out int end,
        out int level,
        out string content)
    {
        ReadLine(source, start, out var line, out end, out _);
        level = 0;
        content = string.Empty;
        if (!TryTrimBlockIndent(line, out var trimmed))
        {
            return false;
        }

        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }
        if (level == 0 || level >= trimmed.Length || !char.IsWhiteSpace(trimmed[level]))
        {
            level = 0;
            return false;
        }

        content = trimmed[(level + 1)..].TrimEnd();
        return true;
    }

    private static bool TryReadListItem(
        string source,
        int start,
        out int end,
        out string prefix,
        out string content,
        out string automationName)
    {
        ReadLine(source, start, out var line, out end, out _);
        prefix = string.Empty;
        content = string.Empty;
        automationName = string.Empty;
        if (!TryTrimBlockIndent(line, out var trimmed))
        {
            return false;
        }

        if (trimmed.Length >= 2 &&
            trimmed[0] is '-' or '*' or '+' &&
            char.IsWhiteSpace(trimmed[1]))
        {
            content = trimmed[2..].TrimStart();
            prefix = "• ";
            automationName = "Markdown unordered list item";
            if (content.Length >= 4 &&
                content[0] == '[' &&
                content[2] == ']' &&
                char.IsWhiteSpace(content[3]) &&
                content[1] is ' ' or 'x' or 'X')
            {
                prefix = content[1] is 'x' or 'X' ? "☑ " : "☐ ";
                content = content[4..].TrimStart();
                automationName = "Markdown task list item";
            }

            return true;
        }

        var digitEnd = 0;
        while (digitEnd < trimmed.Length && digitEnd < 9 && char.IsDigit(trimmed[digitEnd]))
        {
            digitEnd++;
        }
        if (digitEnd == 0 ||
            digitEnd + 1 >= trimmed.Length ||
            trimmed[digitEnd] != '.' ||
            !char.IsWhiteSpace(trimmed[digitEnd + 1]))
        {
            return false;
        }

        prefix = $"{trimmed[..digitEnd]}. ";
        content = trimmed[(digitEnd + 2)..].TrimStart();
        automationName = "Markdown ordered list item";
        return true;
    }

    private static bool TryReadHorizontalRule(string source, int start, out int end)
    {
        ReadLine(source, start, out var line, out end, out _);
        if (!TryTrimBlockIndent(line, out var trimmed))
        {
            return false;
        }

        var marker = new string(trimmed.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return marker.Length >= 3 &&
               marker[0] is '-' or '_' or '*' &&
               marker.All(character => character == marker[0]);
    }

    private static bool TryReadBlockQuote(string source, int start, out int end, out string content)
    {
        end = start;
        content = string.Empty;
        var lines = new List<string>();
        for (var current = start; current < source.Length;)
        {
            ReadLine(source, current, out var line, out var lineEnd, out var nextLineStart);
            if (!TryTrimBlockIndent(line, out var trimmed) || trimmed.Length == 0 || trimmed[0] != '>')
            {
                break;
            }

            var quoted = trimmed[1..];
            if (quoted.Length > 0 && quoted[0] == ' ')
            {
                quoted = quoted[1..];
            }
            lines.Add(quoted);
            end = lineEnd;
            current = nextLineStart;
        }

        if (lines.Count == 0)
        {
            return false;
        }

        content = string.Join(Environment.NewLine, lines);
        return true;
    }

    private static bool TryReadFencedCodeBlock(
        string source,
        int start,
        out int end,
        out string content)
    {
        end = start;
        content = string.Empty;
        ReadLine(source, start, out var openingLine, out _, out var contentStart);
        if (!TryTrimBlockIndent(openingLine, out var opening) ||
            opening.Length < 3 ||
            opening[0] is not '`' and not '~')
        {
            return false;
        }

        var marker = opening[0];
        var markerLength = 0;
        while (markerLength < opening.Length && opening[markerLength] == marker)
        {
            markerLength++;
        }
        if (markerLength < 3)
        {
            return false;
        }

        for (var current = contentStart; current < source.Length;)
        {
            ReadLine(source, current, out var line, out var lineEnd, out var nextLineStart);
            if (TryTrimBlockIndent(line, out var closing) && IsClosingFence(closing, marker, markerLength))
            {
                content = source[contentStart..current];
                end = lineEnd;
                return true;
            }

            current = nextLineStart;
        }

        return false;
    }

    private static bool IsClosingFence(string line, char marker, int minimumLength)
    {
        var markerLength = 0;
        while (markerLength < line.Length && line[markerLength] == marker)
        {
            markerLength++;
        }

        return markerLength >= minimumLength && line[markerLength..].All(char.IsWhiteSpace);
    }

    private static bool TryTrimBlockIndent(string line, out string trimmed)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        trimmed = indent <= 3 ? line[indent..] : string.Empty;
        return indent <= 3;
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

        var alignments = delimiter.Select(ReadTableAlignment).ToArray();
        table = new MarkdownTable(header, rows, alignments);
        return true;
    }

    private static MarkdownTableAlignment ReadTableAlignment(string delimiter)
    {
        var marker = delimiter.Trim();
        return (marker.StartsWith(':'), marker.EndsWith(':')) switch
        {
            (true, true) => MarkdownTableAlignment.Center,
            (false, true) => MarkdownTableAlignment.Right,
            _ => MarkdownTableAlignment.Left
        };
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
                IsEscapedPunctuation(source, index) ||
                IsCombinedEmphasisMarkerStart(source, index) ||
                IsStrongMarkerStart(source, index) ||
                IsStrikethroughMarkerStart(source, index) ||
                IsEmphasisMarkerStart(source, index) ||
                IsBareUrlStart(source, index))
            {
                return index;
            }
        }

        return source.Length;
    }

    private sealed record MarkdownTable(
        string[] Header,
        IReadOnlyList<string[]> Rows,
        IReadOnlyList<MarkdownTableAlignment> Alignments);

    private enum MarkdownTableAlignment
    {
        Left,
        Center,
        Right
    }
}
