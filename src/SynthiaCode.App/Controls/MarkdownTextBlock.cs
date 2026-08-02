using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using SynthiaCode.App.Services;

namespace SynthiaCode.App.Controls;

public sealed class MarkdownTextBlock : TextBlock
{
    private IReadOnlyDictionary<string, string> footnoteDefinitions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> footnoteOrdinals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> footnoteTargets = new(StringComparer.OrdinalIgnoreCase);

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

    public static readonly DependencyProperty EditImageCommandProperty = DependencyProperty.Register(
        nameof(EditImageCommand),
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

    public ICommand? EditImageCommand
    {
        get => (ICommand?)GetValue(EditImageCommandProperty);
        set => SetValue(EditImageCommandProperty, value);
    }

    private static void OnContentPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownTextBlock)dependencyObject).RenderMarkdown();

    private void RenderMarkdown()
    {
        Inlines.Clear();

        footnoteOrdinals.Clear();
        footnoteTargets.Clear();
        var source = ExtractFootnoteDefinitions(
            Markdown ?? string.Empty,
            out footnoteDefinitions);
        var segmentStart = 0;
        for (var lineStart = 0; lineStart < source.Length; lineStart = FindNextLineStart(source, lineStart))
        {
            Inline? block = null;
            var blockEnd = lineStart;
            if (TryReadFencedCodeBlock(source, lineStart, out blockEnd, out var code, out var infoString))
            {
                block = new InlineUIContainer(CreateFencedCodeBlock(code, infoString));
            }
            else if (TryReadMarkdownTable(source, lineStart, out blockEnd, out var table))
            {
                block = new InlineUIContainer(CreateTable(table));
            }
            else if (TryReadDefinitionList(source, lineStart, out blockEnd, out var definitions))
            {
                block = new InlineUIContainer(CreateDefinitionList(definitions));
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
            else if (TryReadNestedList(source, lineStart, out blockEnd, out var nestedList))
            {
                block = new InlineUIContainer(CreateNestedList(nestedList));
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
        if (footnoteOrdinals.Count > 0)
        {
            Inlines.Add(new InlineUIContainer(CreateFootnotes()));
        }
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

            if (source[index] == '!' &&
                TryReadLocalImageLink(source, index, hasImageMarker: true, out var imageEnd, out var imageLabel, out var imageUri, out var imagePath) &&
                TryCreateGeneratedImagePreview(imageLabel, imageUri, imagePath, out var markedPreview))
            {
                inlines.Add(new InlineUIContainer(markedPreview));
                index = imageEnd;
                continue;
            }

            if (source[index] == '<' &&
                TryAppendRawHtml(inlines, source, index, out var htmlEnd))
            {
                index = htmlEnd;
                continue;
            }

            if (source[index] == '!' &&
                TryReadRemoteImageLink(source, index, out var remoteImageEnd, out var remoteImageLabel, out var remoteImageUri))
            {
                inlines.Add(new InlineUIContainer(CreateRemoteImagePreview(remoteImageLabel, remoteImageUri)));
                index = remoteImageEnd;
                continue;
            }

            if (source[index] == '[' &&
                TryReadLocalImageLink(source, index, hasImageMarker: false, out var localImageEnd, out var localImageLabel, out var localImageUri, out var localImagePath) &&
                TryCreateGeneratedImagePreview(localImageLabel, localImageUri, localImagePath, out var linkedPreview))
            {
                inlines.Add(new InlineUIContainer(linkedPreview));
                index = localImageEnd;
                continue;
            }

            if (source[index] == '[' &&
                TryReadFootnoteReference(source, index, out var footnoteEnd, out var footnoteLabel) &&
                footnoteDefinitions.ContainsKey(footnoteLabel))
            {
                AddFootnoteReference(inlines, footnoteLabel);
                index = footnoteEnd;
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

    private StackPanel CreateNestedList(IReadOnlyList<MarkdownListItem> items)
    {
        var list = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };
        BindBlockWidth(list);
        AutomationProperties.SetName(list, "Markdown list");

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var marker = new TextBlock
            {
                Text = item.Prefix,
                MinWidth = 22,
                VerticalAlignment = VerticalAlignment.Top
            };
            AutomationProperties.SetName(marker, item.AutomationName);

            var content = CreateNestedMarkdownTextBlock(item.Content);
            var row = new Grid
            {
                Margin = new Thickness(item.Depth * 24, 1, 0, 1)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            AutomationProperties.SetName(row, $"Markdown list item depth {item.Depth}: {index + 1}");
            if (item.TaskState is not null)
            {
                AutomationProperties.SetHelpText(row, item.TaskState.Value ? "Checked task" : "Unchecked task");
            }
            list.Children.Add(row);
        }

        return list;
    }

    private StackPanel CreateDefinitionList(IReadOnlyList<MarkdownDefinitionItem> definitions)
    {
        var list = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };
        BindBlockWidth(list);
        AutomationProperties.SetName(list, "Markdown definition list");

        foreach (var entry in definitions)
        {
            var term = CreateNestedMarkdownTextBlock(entry.Term);
            term.FontWeight = FontWeights.SemiBold;
            term.Margin = new Thickness(0, 4, 0, 1);
            AutomationProperties.SetName(term, $"Markdown definition term: {entry.Term}");
            list.Children.Add(term);

            foreach (var definitionText in entry.Definitions)
            {
                var definition = CreateNestedMarkdownTextBlock(definitionText);
                definition.Margin = new Thickness(24, 1, 0, 2);
                AutomationProperties.SetName(definition, "Markdown definition");
                list.Children.Add(definition);
            }
        }

        return list;
    }

    private void AddFootnoteReference(InlineCollection inlines, string label)
    {
        if (!footnoteOrdinals.TryGetValue(label, out var ordinal))
        {
            ordinal = footnoteOrdinals.Count + 1;
            footnoteOrdinals[label] = ordinal;
        }

        var reference = new Hyperlink(new Run(ordinal.ToString()))
        {
            BaselineAlignment = BaselineAlignment.Superscript,
            ToolTip = new ToolTip { Content = $"Footnote {ordinal}: {label}" }
        };
        reference.Click += (_, _) =>
        {
            if (footnoteTargets.TryGetValue(label, out var target))
            {
                target.BringIntoView();
                target.Focus();
            }
        };
        AutomationProperties.SetName(reference, $"Markdown footnote reference: {label}");
        inlines.Add(reference);
    }

    private StackPanel CreateFootnotes()
    {
        var footnotes = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0)
        };
        BindBlockWidth(footnotes);
        AutomationProperties.SetName(footnotes, "Markdown footnotes");

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 7)
        };
        divider.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        footnotes.Children.Add(divider);

        foreach (var entry in footnoteOrdinals.OrderBy(item => item.Value))
        {
            var marker = new TextBlock
            {
                Text = $"{entry.Value}.",
                MinWidth = 24,
                VerticalAlignment = VerticalAlignment.Top
            };
            marker.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");

            var definition = CreateNestedMarkdownTextBlock(footnoteDefinitions[entry.Key]);
            var row = new Grid
            {
                Focusable = true,
                Margin = new Thickness(0, 2, 0, 2)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(definition, 1);
            row.Children.Add(marker);
            row.Children.Add(definition);
            AutomationProperties.SetName(row, $"Markdown footnote: {entry.Key}");
            footnoteTargets[entry.Key] = row;
            footnotes.Children.Add(row);
        }

        return footnotes;
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

    private Border CreateFencedCodeBlock(string content, string infoString)
    {
        var codeText = RemoveFenceSeparatingLineEnding(content);
        var languageLabel = MarkdownSyntaxHighlighter.DisplayLabel(infoString);
        var code = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            FontSize = FontSize,
            Foreground = Foreground
        };
        code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        if (MarkdownSyntaxHighlighter.TryResolve(infoString, out _, out var language))
        {
            MarkdownSyntaxHighlighter.Highlight(code, codeText, language);
        }
        else
        {
            code.Text = codeText;
        }
        AutomationProperties.SetName(code, $"Highlighted {languageLabel} code");

        var languageText = new TextBlock
        {
            Text = languageLabel,
            VerticalAlignment = VerticalAlignment.Center
        };
        languageText.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetName(languageText, $"Code language: {languageLabel}");

        var copyButton = new Button
        {
            Content = "Copy",
            Tag = codeText,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = $"Copy {languageLabel} code"
        };
        copyButton.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        copyButton.Click += OnCopyCodeClick;
        AutomationProperties.SetName(copyButton, $"Copy {languageLabel} code");

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(languageText);
        header.Children.Add(copyButton);

        var scroller = new ScrollViewer
        {
            Content = code,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var contentPanel = new StackPanel();
        contentPanel.Children.Add(header);
        contentPanel.Children.Add(scroller);
        var block = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(9, 7, 9, 7),
            Child = contentPanel
        };
        block.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
        block.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        BindBlockWidth(block);
        AutomationProperties.SetName(block, "Markdown fenced code block");
        return block;
    }

    private static void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string code })
        {
            Clipboard.SetText(code);
        }
    }

    private static string RemoveFenceSeparatingLineEnding(string content)
    {
        if (content.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return content[..^2];
        }

        return content.EndsWith('\r') || content.EndsWith('\n')
            ? content[..^1]
            : content;
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

    private bool TryAppendRawHtml(
        InlineCollection inlines,
        string source,
        int start,
        out int end)
    {
        end = start;
        if (source.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            var commentEnd = source.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                return false;
            }

            end = commentEnd + 3;
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
        if (tag == "br" && opening[tagLength..].Trim() is "" or "/")
        {
            inlines.Add(new LineBreak());
            end = openingEnd + 1;
            return true;
        }

        if (tag == "img")
        {
            if (!TryGetHtmlAttribute(opening, "src", out var sourceValue) ||
                !TryGetSafeUri(System.Net.WebUtility.HtmlDecode(sourceValue), out var imageUri))
            {
                return false;
            }

            var label = TryGetHtmlAttribute(opening, "alt", out var alt)
                ? System.Net.WebUtility.HtmlDecode(alt)
                : imageUri.Host;
            inlines.Add(new InlineUIContainer(CreateRemoteImagePreview(label, imageUri)));
            end = openingEnd + 1;
            return true;
        }

        if (!IsSupportedPairedHtmlTag(tag))
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
        switch (tag)
        {
            case "strong":
            case "b":
            {
                var strong = new Bold();
                AppendInlineMarkdown(strong.Inlines, content);
                inlines.Add(strong);
                return true;
            }
            case "em":
            case "i":
            {
                var emphasis = new Italic();
                AppendInlineMarkdown(emphasis.Inlines, content);
                inlines.Add(emphasis);
                return true;
            }
            case "del":
            case "s":
            {
                var deleted = new Span { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                AppendInlineMarkdown(deleted.Inlines, content);
                inlines.Add(deleted);
                return true;
            }
            case "code":
            case "kbd":
            {
                var code = new Run(System.Net.WebUtility.HtmlDecode(content));
                code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                code.SetResourceReference(TextElement.BackgroundProperty, "SubtleBrush");
                AutomationProperties.SetName(code, "Inline HTML code");
                inlines.Add(code);
                return true;
            }
            case "a":
            {
                if (!TryGetHtmlAttribute(opening, "href", out var href) ||
                    !TryGetSafeUri(System.Net.WebUtility.HtmlDecode(href), out var target))
                {
                    end = start;
                    return false;
                }

                var link = new Hyperlink
                {
                    NavigateUri = target,
                    ToolTip = new ToolTip { Content = target.AbsoluteUri }
                };
                AppendInlineMarkdown(link.Inlines, content);
                link.RequestNavigate += OnLinkRequestNavigate;
                inlines.Add(link);
                return true;
            }
            case "pre":
            {
                var preformatted = new Run(System.Net.WebUtility.HtmlDecode(content));
                preformatted.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                AutomationProperties.SetName(preformatted, "Raw HTML preformatted text");
                inlines.Add(preformatted);
                return true;
            }
            case "blockquote":
            {
                var quote = new Italic();
                AutomationProperties.SetName(quote, "Raw HTML block quote");
                AppendInlineMarkdown(quote.Inlines, content);
                inlines.Add(quote);
                return true;
            }
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
            {
                var heading = CreateHeading(tag[1] - '0', content);
                inlines.Add(heading);
                return true;
            }
            default:
            {
                var block = new Span();
                if (inlines.Count > 0)
                {
                    block.Inlines.Add(new LineBreak());
                }
                AppendInlineMarkdown(block.Inlines, content);
                block.Inlines.Add(new LineBreak());
                inlines.Add(block);
                return true;
            }
        }
    }

    private static bool IsSupportedPairedHtmlTag(string tag) =>
        tag is "strong" or "b" or "em" or "i" or "del" or "s" or "code" or "kbd" or
            "a" or "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "blockquote" or "pre";

    private static bool TryGetHtmlAttribute(string opening, string attributeName, out string value)
    {
        value = string.Empty;
        for (var index = 0; index < opening.Length;)
        {
            while (index < opening.Length && !char.IsLetter(opening[index]))
            {
                index++;
            }

            var nameStart = index;
            while (index < opening.Length &&
                   (char.IsLetterOrDigit(opening[index]) || opening[index] is '-' or '_'))
            {
                index++;
            }
            if (nameStart == index ||
                !opening[nameStart..index].Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            while (index < opening.Length && char.IsWhiteSpace(opening[index]))
            {
                index++;
            }
            if (index >= opening.Length || opening[index] != '=')
            {
                continue;
            }

            index++;
            while (index < opening.Length && char.IsWhiteSpace(opening[index]))
            {
                index++;
            }
            if (index >= opening.Length)
            {
                return false;
            }

            if (opening[index] is '"' or '\'')
            {
                var quote = opening[index++];
                var valueEnd = opening.IndexOf(quote, index);
                if (valueEnd < 0)
                {
                    return false;
                }

                value = opening[index..valueEnd];
                return true;
            }

            var unquotedEnd = index;
            while (unquotedEnd < opening.Length &&
                   !char.IsWhiteSpace(opening[unquotedEnd]) &&
                   opening[unquotedEnd] != '/')
            {
                unquotedEnd++;
            }
            value = opening[index..unquotedEnd];
            return value.Length > 0;
        }

        return false;
    }

    private Border CreateRemoteImagePreview(string label, Uri target)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnDemand;
        bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
        bitmap.DecodePixelWidth = 960;
        bitmap.UriSource = target;
        bitmap.EndInit();

        var image = new Image
        {
            Source = bitmap,
            MaxWidth = 720,
            MaxHeight = 480,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(image, $"Remote image: {label}");

        var failureText = new TextBlock
        {
            Text = $"Image unavailable: {label}",
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        failureText.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
        image.ImageFailed += (_, _) =>
        {
            image.Visibility = Visibility.Collapsed;
            failureText.Visibility = Visibility.Visible;
        };

        var linkText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        AddLink(linkText.Inlines, label, target);

        var content = new StackPanel();
        content.Children.Add(image);
        content.Children.Add(failureText);
        content.Children.Add(linkText);

        var preview = new Border
        {
            Child = content,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ToolTip = target.AbsoluteUri
        };
        preview.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
        preview.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        preview.SetBinding(
            FrameworkElement.MaxWidthProperty,
            new Binding(nameof(ActualWidth)) { Source = this });
        AutomationProperties.SetName(preview, $"Remote image preview: {label}");
        return preview;
    }

    private bool TryCreateGeneratedImagePreview(
        string label,
        Uri target,
        string path,
        out Border preview)
    {
        preview = null!;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 960;
            bitmap.UriSource = target;
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new Image
            {
                Source = bitmap,
                MaxWidth = 720,
                MaxHeight = 480,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(image, $"Generated image: {label}");

            var previewButton = new Button
            {
                Content = image,
                Tag = path,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"Edit {label}"
            };
            previewButton.SetResourceReference(Control.FocusVisualStyleProperty, "FocusVisual");
            previewButton.Click += OnGeneratedImagePreviewClick;
            AutomationProperties.SetName(previewButton, $"Edit generated image: {label}");

            var linkText = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            AddLink(linkText.Inlines, label, target);

            var content = new StackPanel();
            content.Children.Add(previewButton);
            content.Children.Add(linkText);
            if (EditImageCommand?.CanExecute(path) == true)
            {
                var editButton = new Button
                {
                    Content = "Edit image",
                    Command = EditImageCommand,
                    CommandParameter = path,
                    Margin = new Thickness(0, 8, 0, 0),
                    Padding = new Thickness(12, 5, 12, 5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Edit the whole image or mark a region to change"
                };
                editButton.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
                AutomationProperties.SetName(editButton, $"Edit generated image: {label}");
                content.Children.Add(editButton);
            }

            preview = new Border
            {
                Child = content,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 8, 0, 8),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ToolTip = path
            };
            preview.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            preview.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            preview.SetBinding(
                FrameworkElement.MaxWidthProperty,
                new Binding(nameof(ActualWidth)) { Source = this });
            AutomationProperties.SetName(preview, $"Generated image preview: {label}");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            NotSupportedException or
            UnauthorizedAccessException or
            FileFormatException)
        {
            return false;
        }
    }

    private void OnGeneratedImagePreviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        if (EditImageCommand?.CanExecute(path) == true)
        {
            EditImageCommand.Execute(path);
        }
        else if (LocalImageResourcePolicy.TryCreateSupportedUri(path, out var target, out _) &&
                 LinkCommand?.CanExecute(target) == true)
        {
            LinkCommand.Execute(target);
        }
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
        out int end,
        out IReadOnlyList<MarkdownDefinitionItem> definitions)
    {
        end = start;
        var parsed = new List<MarkdownDefinitionItem>();
        var current = start;
        while (current < source.Length)
        {
            if (!TryReadDefinitionItem(source, current, out var itemEnd, out var nextStart, out var item))
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

    private static bool TryReadLocalImageLink(
        string source,
        int start,
        bool hasImageMarker,
        out int end,
        out string label,
        out Uri target,
        out string path)
    {
        end = start;
        label = string.Empty;
        target = null!;
        path = string.Empty;

        var linkStart = hasImageMarker ? start + 1 : start;
        if (linkStart >= source.Length ||
            source[linkStart] != '[' ||
            (hasImageMarker && source[start] != '!'))
        {
            return false;
        }

        var labelEnd = source.IndexOf("](", linkStart + 1, StringComparison.Ordinal);
        if (labelEnd <= linkStart + 1)
        {
            return false;
        }

        var targetEnd = source.IndexOf(')', labelEnd + 2);
        if (targetEnd < 0 ||
            !LocalImageResourcePolicy.TryCreateSupportedUri(
                source[(labelEnd + 2)..targetEnd],
                out target,
                out path))
        {
            return false;
        }

        label = source[(linkStart + 1)..labelEnd];
        end = targetEnd + 1;
        return true;
    }

    private static bool TryReadNestedList(
        string source,
        int start,
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
            if (!TryParseNestedListLine(line, out var indent, out var prefix, out var content, out var automationName, out var taskState))
            {
                break;
            }

            baseIndent = baseIndent < 0 ? indent : baseIndent;
            if (indent < baseIndent)
            {
                break;
            }

            var depth = Math.Min(previousDepth + 1, Math.Max(0, (indent - baseIndent + 1) / 2));
            parsed.Add(new MarkdownListItem(depth, prefix, content, automationName, taskState));
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

    private static bool TryReadRemoteImageLink(
        string source,
        int start,
        out int end,
        out string label,
        out Uri target)
    {
        end = start;
        label = string.Empty;
        target = null!;
        if (start + 1 >= source.Length || source[start] != '!' || source[start + 1] != '[')
        {
            return false;
        }

        var labelEnd = source.IndexOf("](", start + 2, StringComparison.Ordinal);
        if (labelEnd <= start + 2)
        {
            return false;
        }

        var targetEnd = source.IndexOf(')', labelEnd + 2);
        if (targetEnd < 0 ||
            !TryGetSafeUri(source[(labelEnd + 2)..targetEnd], out target))
        {
            return false;
        }

        label = source[(start + 2)..labelEnd];
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

    private sealed record MarkdownTable(
        string[] Header,
        IReadOnlyList<string[]> Rows,
        IReadOnlyList<MarkdownTableAlignment> Alignments);

    private sealed record MarkdownListItem(
        int Depth,
        string Prefix,
        string Content,
        string AutomationName,
        bool? TaskState);

    private sealed record MarkdownDefinitionItem(
        string Term,
        IReadOnlyList<string> Definitions);

    private enum MarkdownTableAlignment
    {
        Left,
        Center,
        Right
    }
}
