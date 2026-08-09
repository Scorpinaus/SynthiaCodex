using System.Net;
using System.Text;

namespace SynthiaCode.Presentation.Markdown;

public static class MarkdownDocumentParser
{
    public static MarkdownDocument Parse(string? markdown)
    {
        var source = ExtractFootnoteDefinitions(
            markdown ?? string.Empty,
            out var footnoteDefinitions);
        var blocks = new List<MarkdownBlock>();
        var segmentStart = 0;

        for (var lineStart = 0; lineStart < source.Length; lineStart = FindNextLineStart(source, lineStart))
        {
            MarkdownBlock? block = null;
            var blockEnd = lineStart;

            if (TryReadFencedCodeBlock(source, lineStart, out blockEnd, out var code, out var infoString))
            {
                block = new MarkdownCodeBlock(code, infoString);
            }
            else if (TryReadMarkdownTable(source, lineStart, footnoteDefinitions, out blockEnd, out var table))
            {
                block = table;
            }
            else if (TryReadDefinitionList(source, lineStart, footnoteDefinitions, out blockEnd, out var definitions))
            {
                block = new MarkdownDefinitionListBlock(definitions);
            }
            else if (TryReadBlockQuote(source, lineStart, out blockEnd, out var quote))
            {
                block = new MarkdownQuoteBlock(quote);
            }
            else if (TryReadHeading(source, lineStart, out blockEnd, out var headingLevel, out var headingContent))
            {
                block = new MarkdownHeadingBlock(
                    headingLevel,
                    ParseInlines(headingContent, footnoteDefinitions));
            }
            else if (TryReadHorizontalRule(source, lineStart, out blockEnd))
            {
                block = new MarkdownHorizontalRuleBlock();
            }
            else if (TryReadNestedList(source, lineStart, footnoteDefinitions, out blockEnd, out var nestedList))
            {
                block = new MarkdownNestedListBlock(nestedList);
            }
            else if (TryReadListItem(
                         source,
                         lineStart,
                         out blockEnd,
                         out var listPrefix,
                         out var listContent,
                         out var listAutomationName))
            {
                block = new MarkdownListItemBlock(
                    listPrefix,
                    listAutomationName,
                    ParseInlines(listContent, footnoteDefinitions));
            }

            if (block is null)
            {
                continue;
            }

            AddInlineBlock(blocks, source[segmentStart..lineStart], footnoteDefinitions);
            blocks.Add(block);
            segmentStart = blockEnd;
            lineStart = blockEnd;
        }

        AddInlineBlock(blocks, source[segmentStart..], footnoteDefinitions);
        return new MarkdownDocument(blocks, footnoteDefinitions);
    }

    public static IReadOnlyList<MarkdownInline> ParseInlines(
        string? source,
        IReadOnlyDictionary<string, string>? footnoteDefinitions = null)
    {
        source ??= string.Empty;
        footnoteDefinitions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inlines = new List<MarkdownInline>();

        for (var index = 0; index < source.Length;)
        {
            if (IsEscapedPunctuation(source, index))
            {
                AddText(inlines, source[(index + 1)..(index + 2)]);
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

                AddText(inlines, source[index..markerEnd]);
                index = markerEnd;
                continue;
            }

            if (source[index] == '`' && TryReadCodeSpan(source, index, out var codeEnd))
            {
                inlines.Add(new MarkdownCodeInline(source[(index + 1)..(codeEnd - 1)]));
                index = codeEnd;
                continue;
            }

            if (IsCombinedEmphasisMarkerStart(source, index) &&
                TryReadDelimited(source, index, source.Substring(index, 3), out var combinedEnd, out var combinedContent))
            {
                inlines.Add(new MarkdownStrongEmphasisInline(ParseInlines(combinedContent, footnoteDefinitions)));
                index = combinedEnd;
                continue;
            }

            if (IsStrongMarkerStart(source, index) &&
                TryReadStrong(source, index, out var strongEnd, out var strongContent))
            {
                inlines.Add(new MarkdownStrongInline(ParseInlines(strongContent, footnoteDefinitions)));
                index = strongEnd;
                continue;
            }

            if (IsStrikethroughMarkerStart(source, index) &&
                TryReadDelimited(source, index, "~~", out var strikeEnd, out var strikeContent))
            {
                inlines.Add(new MarkdownStrikethroughInline(ParseInlines(strikeContent, footnoteDefinitions)));
                index = strikeEnd;
                continue;
            }

            if (IsEmphasisMarkerStart(source, index) &&
                TryReadDelimited(source, index, source[index].ToString(), out var emphasisEnd, out var emphasisContent))
            {
                inlines.Add(new MarkdownEmphasisInline(ParseInlines(emphasisContent, footnoteDefinitions)));
                index = emphasisEnd;
                continue;
            }

            if (source[index] == '!' &&
                TryReadLinkSyntax(source, index + 1, out var imageEnd, out var imageLabel, out var imageDestination))
            {
                inlines.Add(new MarkdownImageInline(
                    imageLabel,
                    imageDestination,
                    source[index..imageEnd],
                    HasImageMarker: true));
                index = imageEnd;
                continue;
            }

            if (source[index] == '<' &&
                TryReadHtml(source, index, footnoteDefinitions, out var htmlEnd, out var html))
            {
                if (html.Kind != MarkdownHtmlKind.Comment)
                {
                    inlines.Add(html);
                }
                index = htmlEnd;
                continue;
            }

            if (source[index] == '[' &&
                TryReadFootnoteReference(source, index, out var footnoteEnd, out var footnoteLabel) &&
                footnoteDefinitions.ContainsKey(footnoteLabel))
            {
                inlines.Add(new MarkdownFootnoteReferenceInline(footnoteLabel));
                index = footnoteEnd;
                continue;
            }

            if (source[index] == '[' &&
                TryReadLinkSyntax(source, index, out var linkEnd, out var label, out var destination))
            {
                inlines.Add(new MarkdownLinkInline(label, destination, source[index..linkEnd]));
                index = linkEnd;
                continue;
            }

            if (source[index] == '[' && TryReadUnfinishedMarkdownLink(source, index, out var unfinishedEnd))
            {
                AddText(inlines, source[index..unfinishedEnd]);
                index = unfinishedEnd;
                continue;
            }

            if (source[index] == '<' &&
                TryReadAutolink(source, index, out var autolinkEnd, out var autolink))
            {
                inlines.Add(new MarkdownLinkInline(autolink, autolink, source[index..autolinkEnd]));
                index = autolinkEnd;
                continue;
            }

            if (IsBareUrlStart(source, index) &&
                TryReadBareUrl(source, index, out var urlEnd, out var bareUrl))
            {
                inlines.Add(new MarkdownLinkInline(bareUrl, bareUrl, source[index..urlEnd]));
                index = urlEnd;
                continue;
            }

            var plainEnd = FindNextCandidate(source, index + 1);
            AddText(inlines, source[index..plainEnd]);
            index = plainEnd;
        }

        return inlines;
    }

    private static void AddInlineBlock(
        ICollection<MarkdownBlock> blocks,
        string source,
        IReadOnlyDictionary<string, string> footnoteDefinitions)
    {
        if (source.Length > 0)
        {
            blocks.Add(new MarkdownInlineBlock(ParseInlines(source, footnoteDefinitions)));
        }
    }

    private static void AddText(List<MarkdownInline> inlines, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (inlines.LastOrDefault() is MarkdownTextInline previous)
        {
            inlines[^1] = new MarkdownTextInline(previous.Text + text);
            return;
        }

        inlines.Add(new MarkdownTextInline(text));
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
            prefix = "\u2022 ";
            automationName = "Markdown unordered list item";
            if (content.Length >= 4 &&
                content[0] == '[' &&
                content[2] == ']' &&
                char.IsWhiteSpace(content[3]) &&
                content[1] is ' ' or 'x' or 'X')
            {
                prefix = content[1] is 'x' or 'X' ? "\u2611 " : "\u2610 ";
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
        out string content,
        out string infoString)
    {
        end = start;
        content = string.Empty;
        infoString = string.Empty;
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
        infoString = opening[markerLength..].Trim();

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

    private static bool TryReadDefinitionList(
        string source,
        int start,
        IReadOnlyDictionary<string, string> footnoteDefinitions,
        out int end,
        out IReadOnlyList<MarkdownDefinitionItem> definitions)
    {
        end = start;
        var parsed = new List<MarkdownDefinitionItem>();
        var current = start;
        while (current < source.Length)
        {
            if (!TryReadDefinitionItem(
                    source,
                    current,
                    footnoteDefinitions,
                    out var itemEnd,
                    out var nextStart,
                    out var item))
            {
                break;
            }

            parsed.Add(item);
            end = itemEnd;
            current = nextStart;

            if (current < source.Length)
            {
                ReadLine(source, current, out var separator, out _, out var afterSeparator);
                if (string.IsNullOrWhiteSpace(separator))
                {
                    current = afterSeparator;
                }
            }
        }

        if (parsed.Count == 0)
        {
            definitions = [];
            end = start;
            return false;
        }

        definitions = parsed;
        return true;
    }

    private static bool TryReadDefinitionItem(
        string source,
        int start,
        IReadOnlyDictionary<string, string> footnoteDefinitions,
        out int end,
        out int nextStart,
        out MarkdownDefinitionItem item)
    {
        end = start;
        nextStart = start;
        item = null!;
        ReadLine(source, start, out var termLine, out _, out var definitionStart);
        var term = termLine.Trim();
        if (term.Length == 0 || definitionStart >= source.Length)
        {
            return false;
        }

        var values = new List<string>();
        for (var current = definitionStart; current < source.Length;)
        {
            ReadLine(source, current, out var line, out var lineEnd, out var followingLineStart);
            var trimmed = line.TrimStart();
            if (trimmed.Length < 2 || trimmed[0] != ':' || !char.IsWhiteSpace(trimmed[1]))
            {
                break;
            }

            values.Add(trimmed[2..].TrimStart());
            end = lineEnd;
            nextStart = followingLineStart;
            current = followingLineStart;
        }

        if (values.Count == 0)
        {
            return false;
        }

        item = new MarkdownDefinitionItem(term, values);
        return true;
    }

    private static bool TryReadMarkdownTable(
        string source,
        int start,
        IReadOnlyDictionary<string, string> footnoteDefinitions,
        out int end,
        out MarkdownTableBlock table)
    {
        end = start;
        table = null!;
        ReadLine(source, start, out var headerLine, out _, out var delimiterStart);
        if (!TryParseTableRow(headerLine, out var header) || delimiterStart >= source.Length)
        {
            return false;
        }

        ReadLine(source, delimiterStart, out var delimiterLine, out var delimiterEnd, out var rowStart);
        if (!TryParseTableRow(delimiterLine, out var delimiters) ||
            delimiters.Length != header.Length ||
            !IsDelimiterRow(delimiters))
        {
            return false;
        }

        var rows = new List<IReadOnlyList<string>>();
        end = delimiterEnd;
        for (var current = rowStart; current < source.Length;)
        {
            ReadLine(source, current, out var rowLine, out var rowEnd, out var nextRowStart);
            if (!TryParseTableRow(rowLine, out var cells) || cells.Length != header.Length)
            {
                break;
            }

            rows.Add(cells);
            end = rowEnd;
            current = nextRowStart;
        }

        table = new MarkdownTableBlock(
            header,
            rows,
            delimiters.Select(ReadTableAlignment).ToArray());
        return true;
    }

    private static MarkdownTableAlignment ReadTableAlignment(string delimiter)
    {
        var value = delimiter.Trim();
        var left = value.StartsWith(':');
        var right = value.EndsWith(':');
        return left && right
            ? MarkdownTableAlignment.Center
            : right
                ? MarkdownTableAlignment.Right
                : MarkdownTableAlignment.Left;
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
        var value = cell.Trim().Trim(':');
        return value.Length >= 3 && value.All(character => character == '-');
    });

    private static string ExtractFootnoteDefinitions(
        string source,
        out IReadOnlyDictionary<string, string> definitions)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var renderedSource = new StringBuilder(source.Length);
        char? fenceMarker = null;
        var fenceLength = 0;
        for (var current = 0; current < source.Length;)
        {
            ReadLine(source, current, out var line, out _, out var nextLineStart);
            if (fenceMarker is not null)
            {
                renderedSource.Append(source, current, nextLineStart - current);
                if (TryTrimBlockIndent(line, out var closing) &&
                    IsClosingFence(closing, fenceMarker.Value, fenceLength))
                {
                    fenceMarker = null;
                    fenceLength = 0;
                }
                current = nextLineStart;
                continue;
            }

            if (TryReadOpeningFence(line, out var openingMarker, out var openingLength))
            {
                fenceMarker = openingMarker;
                fenceLength = openingLength;
                renderedSource.Append(source, current, nextLineStart - current);
                current = nextLineStart;
                continue;
            }

            if (!TryParseFootnoteDefinitionLine(line, out var label, out var definition))
            {
                renderedSource.Append(source, current, nextLineStart - current);
                current = nextLineStart;
                continue;
            }

            var definitionText = new StringBuilder(definition);
            current = nextLineStart;
            while (current < source.Length)
            {
                ReadLine(source, current, out var continuation, out _, out var followingLineStart);
                var indent = 0;
                while (indent < continuation.Length && continuation[indent] == ' ')
                {
                    indent++;
                }
                if (indent < 2 || indent == continuation.Length)
                {
                    break;
                }

                definitionText.AppendLine();
                definitionText.Append(continuation[indent..]);
                current = followingLineStart;
            }

            parsed[label] = definitionText.ToString();
        }

        definitions = parsed;
        return renderedSource.ToString();
    }

    private static bool TryReadOpeningFence(string line, out char marker, out int markerLength)
    {
        marker = default;
        markerLength = 0;
        if (!TryTrimBlockIndent(line, out var trimmed) ||
            trimmed.Length < 3 ||
            trimmed[0] is not '`' and not '~')
        {
            return false;
        }

        marker = trimmed[0];
        while (markerLength < trimmed.Length && trimmed[markerLength] == marker)
        {
            markerLength++;
        }

        return markerLength >= 3;
    }

    private static bool TryParseFootnoteDefinitionLine(
        string line,
        out string label,
        out string definition)
    {
        label = string.Empty;
        definition = string.Empty;
        var trimmed = line.TrimStart();
        if (line.Length - trimmed.Length > 3 || !trimmed.StartsWith("[^", StringComparison.Ordinal))
        {
            return false;
        }

        var markerEnd = trimmed.IndexOf("]:", 2, StringComparison.Ordinal);
        if (markerEnd <= 2)
        {
            return false;
        }

        label = trimmed[2..markerEnd].Trim();
        if (label.Length == 0 || label.Any(char.IsWhiteSpace))
        {
            label = string.Empty;
            return false;
        }

        definition = trimmed[(markerEnd + 2)..].TrimStart();
        return true;
    }

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

    private static bool TryReadFootnoteReference(
        string source,
        int start,
        out int end,
        out string label)
    {
        end = start;
        label = string.Empty;
        if (start + 3 >= source.Length ||
            source[start] != '[' ||
            source[start + 1] != '^')
        {
            return false;
        }

        var closing = source.IndexOf(']', start + 2);
        if (closing <= start + 2)
        {
            return false;
        }

        label = source[(start + 2)..closing];
        if (label.Any(char.IsWhiteSpace))
        {
            label = string.Empty;
            return false;
        }

        end = closing + 1;
        return true;
    }

    private static bool TryReadLinkSyntax(
        string source,
        int start,
        out int end,
        out string label,
        out string destination)
    {
        end = start;
        label = string.Empty;
        destination = string.Empty;
        if (start >= source.Length || source[start] != '[')
        {
            return false;
        }

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

        label = source[(start + 1)..labelEnd];
        destination = source[(labelEnd + 2)..targetEnd];
        end = targetEnd + 1;
        return true;
    }

    private static bool TryReadNestedList(
        string source,
        int start,
        IReadOnlyDictionary<string, string> footnoteDefinitions,
        out int end,
        out IReadOnlyList<MarkdownListItem> items)
    {
        end = start;
        var parsed = new List<MarkdownListItem>();
        var baseIndent = -1;
        var previousDepth = 0;
        for (var current = start; current < source.Length;)
        {
            ReadLine(source, current, out var line, out var lineEnd, out var nextLineStart);
            if (!TryParseNestedListLine(
                    line,
                    out var indent,
                    out var prefix,
                    out var content,
                    out var automationName,
                    out var taskState))
            {
                break;
            }

            baseIndent = baseIndent < 0 ? indent : baseIndent;
            if (indent < baseIndent)
            {
                break;
            }

            var depth = Math.Min(previousDepth + 1, Math.Max(0, (indent - baseIndent + 1) / 2));
            parsed.Add(new MarkdownListItem(
                depth,
                prefix,
                automationName,
                taskState,
                content));
            previousDepth = depth;
            end = lineEnd;
            current = nextLineStart;
        }

        if (parsed.Count < 2 || parsed.All(item => item.Depth == 0))
        {
            items = [];
            end = start;
            return false;
        }

        items = parsed;
        return true;
    }

    private static bool TryParseNestedListLine(
        string line,
        out int indent,
        out string prefix,
        out string content,
        out string automationName,
        out bool? taskState)
    {
        indent = 0;
        prefix = string.Empty;
        content = string.Empty;
        automationName = string.Empty;
        taskState = null;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }
        if (indent == line.Length)
        {
            return false;
        }

        var item = line[indent..];
        if (item.Length >= 2 &&
            item[0] is '-' or '*' or '+' &&
            char.IsWhiteSpace(item[1]))
        {
            prefix = "\u2022 ";
            automationName = "Markdown unordered list item";
            content = item[2..].TrimStart();
            if (content.Length >= 4 &&
                content[0] == '[' &&
                content[2] == ']' &&
                char.IsWhiteSpace(content[3]) &&
                content[1] is ' ' or 'x' or 'X')
            {
                taskState = content[1] is 'x' or 'X';
                prefix = taskState.Value ? "\u2611 " : "\u2610 ";
                automationName = "Markdown task list item";
                content = content[4..].TrimStart();
            }
            return true;
        }

        var digitEnd = 0;
        while (digitEnd < item.Length && digitEnd < 9 && char.IsDigit(item[digitEnd]))
        {
            digitEnd++;
        }
        if (digitEnd == 0 ||
            digitEnd + 1 >= item.Length ||
            item[digitEnd] is not '.' and not ')' ||
            !char.IsWhiteSpace(item[digitEnd + 1]))
        {
            return false;
        }

        prefix = $"{item[..(digitEnd + 1)]} ";
        content = item[(digitEnd + 2)..].TrimStart();
        automationName = "Markdown ordered list item";
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

    private static bool TryReadAutolink(string source, int start, out int end, out string target)
    {
        end = start;
        target = string.Empty;
        var closing = source.IndexOf('>', start + 1);
        if (closing < 0)
        {
            return false;
        }

        var candidate = source[(start + 1)..closing];
        if (!IsWebDestination(candidate))
        {
            return false;
        }

        target = candidate;
        end = closing + 1;
        return true;
    }

    private static bool IsBareUrlStart(string source, int start)
    {
        if (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] is '_' or '-'))
        {
            return false;
        }

        return IsWebDestination(source[start..]);
    }

    private static bool TryReadBareUrl(string source, int start, out int end, out string target)
    {
        end = start;
        target = string.Empty;
        var candidateEnd = start;
        while (candidateEnd < source.Length &&
               !char.IsWhiteSpace(source[candidateEnd]) &&
               source[candidateEnd] is not '<' and not '>')
        {
            candidateEnd++;
        }

        while (candidateEnd > start && source[candidateEnd - 1] is '.' or ',' or ';' or ':' or '!' or '?')
        {
            candidateEnd--;
        }

        if (candidateEnd == start)
        {
            return false;
        }

        target = source[start..candidateEnd];
        end = candidateEnd;
        return IsWebDestination(target);
    }

    private static bool IsWebDestination(string value) =>
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadHtml(
        string source,
        int start,
        IReadOnlyDictionary<string, string> footnoteDefinitions,
        out int end,
        out MarkdownHtmlInline html)
    {
        end = start;
        html = null!;
        if (source.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            var commentEnd = source.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                return false;
            }

            end = commentEnd + 3;
            html = new MarkdownHtmlInline(
                MarkdownHtmlKind.Comment,
                "!--",
                source[start..end],
                string.Empty,
                EmptyAttributes,
                []);
            return true;
        }

        var openingEnd = source.IndexOf('>', start + 1);
        if (openingEnd < 0)
        {
            return false;
        }

        var opening = source[(start + 1)..openingEnd].Trim();
        var tagLength = 0;
        while (tagLength < opening.Length && char.IsLetterOrDigit(opening[tagLength]))
        {
            tagLength++;
        }
        if (tagLength == 0)
        {
            return false;
        }

        var tag = opening[..tagLength].ToLowerInvariant();
        var attributes = ReadHtmlAttributes(opening[tagLength..]);
        if (tag == "br" && opening[tagLength..].Trim() is "" or "/")
        {
            end = openingEnd + 1;
            html = new MarkdownHtmlInline(
                MarkdownHtmlKind.LineBreak,
                tag,
                source[start..end],
                string.Empty,
                attributes,
                []);
            return true;
        }

        if (tag == "img")
        {
            end = openingEnd + 1;
            html = new MarkdownHtmlInline(
                MarkdownHtmlKind.Image,
                tag,
                source[start..end],
                string.Empty,
                attributes,
                []);
            return true;
        }

        if (!TryGetHtmlKind(tag, out var kind, out var headingLevel))
        {
            return false;
        }

        var closingTag = $"</{tag}>";
        var closingStart = source.IndexOf(closingTag, openingEnd + 1, StringComparison.OrdinalIgnoreCase);
        if (closingStart < 0)
        {
            return false;
        }

        var content = source[(openingEnd + 1)..closingStart];
        end = closingStart + closingTag.Length;
        html = new MarkdownHtmlInline(
            kind,
            tag,
            source[start..end],
            content,
            attributes,
            kind is MarkdownHtmlKind.Code or MarkdownHtmlKind.Preformatted
                ? []
                : ParseInlines(content, footnoteDefinitions),
            headingLevel);
        return true;
    }

    private static bool TryGetHtmlKind(string tag, out MarkdownHtmlKind kind, out int headingLevel)
    {
        headingLevel = 0;
        kind = tag switch
        {
            "strong" or "b" => MarkdownHtmlKind.Strong,
            "em" or "i" => MarkdownHtmlKind.Emphasis,
            "del" or "s" => MarkdownHtmlKind.Strikethrough,
            "code" or "kbd" => MarkdownHtmlKind.Code,
            "a" => MarkdownHtmlKind.Link,
            "pre" => MarkdownHtmlKind.Preformatted,
            "blockquote" => MarkdownHtmlKind.BlockQuote,
            "p" or "div" => MarkdownHtmlKind.Block,
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => MarkdownHtmlKind.Heading,
            _ => default
        };
        if (kind == default && tag is not "strong" and not "b")
        {
            return false;
        }

        if (kind == MarkdownHtmlKind.Heading)
        {
            headingLevel = tag[1] - '0';
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string> ReadHtmlAttributes(string source)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < source.Length;)
        {
            while (index < source.Length && !char.IsLetter(source[index]))
            {
                index++;
            }

            var nameStart = index;
            while (index < source.Length &&
                   (char.IsLetterOrDigit(source[index]) || source[index] is '-' or '_'))
            {
                index++;
            }
            if (nameStart == index)
            {
                continue;
            }

            var name = source[nameStart..index];
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }
            if (index >= source.Length || source[index] != '=')
            {
                continue;
            }

            index++;
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }
            if (index >= source.Length)
            {
                break;
            }

            string value;
            if (source[index] is '"' or '\'')
            {
                var quote = source[index++];
                var valueEnd = source.IndexOf(quote, index);
                if (valueEnd < 0)
                {
                    break;
                }
                value = source[index..valueEnd];
                index = valueEnd + 1;
            }
            else
            {
                var valueEnd = index;
                while (valueEnd < source.Length &&
                       !char.IsWhiteSpace(source[valueEnd]) &&
                       source[valueEnd] != '/')
                {
                    valueEnd++;
                }
                value = source[index..valueEnd];
                index = valueEnd;
            }

            attributes[name] = WebUtility.HtmlDecode(value);
        }

        return attributes;
    }

    private static int FindNextCandidate(string source, int start)
    {
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] is '[' or '<' or '`' ||
                (source[index] == '!' && index + 1 < source.Length && source[index + 1] == '[') ||
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

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
