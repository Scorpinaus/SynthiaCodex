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
using MarkdownModel = SynthiaCode.Presentation.Markdown;

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
        var document = MarkdownModel.MarkdownDocumentParser.Parse(Markdown);
        footnoteDefinitions = document.FootnoteDefinitions;

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case MarkdownModel.MarkdownInlineBlock inlineBlock:
                    AppendMarkdownInlines(Inlines, inlineBlock.Inlines);
                    break;
                case MarkdownModel.MarkdownCodeBlock codeBlock:
                    Inlines.Add(new InlineUIContainer(CreateFencedCodeBlock(codeBlock.Content, codeBlock.InfoString)));
                    break;
                case MarkdownModel.MarkdownTableBlock tableBlock:
                    Inlines.Add(new InlineUIContainer(CreateTable(tableBlock)));
                    break;
                case MarkdownModel.MarkdownDefinitionListBlock definitionBlock:
                    Inlines.Add(new InlineUIContainer(CreateDefinitionList(definitionBlock.Items)));
                    break;
                case MarkdownModel.MarkdownQuoteBlock quoteBlock:
                    Inlines.Add(new InlineUIContainer(CreateBlockQuote(quoteBlock.Content)));
                    break;
                case MarkdownModel.MarkdownHeadingBlock headingBlock:
                    Inlines.Add(CreateHeading(headingBlock.Level, headingBlock.Inlines));
                    break;
                case MarkdownModel.MarkdownHorizontalRuleBlock:
                    Inlines.Add(new InlineUIContainer(CreateHorizontalRule()));
                    break;
                case MarkdownModel.MarkdownNestedListBlock nestedListBlock:
                    Inlines.Add(new InlineUIContainer(CreateNestedList(nestedListBlock.Items)));
                    break;
                case MarkdownModel.MarkdownListItemBlock listItemBlock:
                    Inlines.Add(CreateListItem(
                        listItemBlock.Prefix,
                        listItemBlock.Inlines,
                        listItemBlock.AutomationName));
                    break;
            }
        }

        if (footnoteOrdinals.Count > 0)
        {
            Inlines.Add(new InlineUIContainer(CreateFootnotes()));
        }
    }

    private void AppendInlineMarkdown(InlineCollection inlines, string source)
        => AppendMarkdownInlines(
            inlines,
            MarkdownModel.MarkdownDocumentParser.ParseInlines(source, footnoteDefinitions));

    private void AppendMarkdownInlines(
        InlineCollection inlines,
        IReadOnlyList<MarkdownModel.MarkdownInline> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case MarkdownModel.MarkdownTextInline text:
                    inlines.Add(new Run(text.Text));
                    break;
                case MarkdownModel.MarkdownCodeInline codeInline:
                {
                    var code = new Run(codeInline.Text);
                    code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                    code.SetResourceReference(TextElement.BackgroundProperty, "SubtleBrush");
                    AutomationProperties.SetName(code, "Inline code");
                    inlines.Add(code);
                    break;
                }
                case MarkdownModel.MarkdownStrongEmphasisInline combinedInline:
                {
                    var combined = new Bold();
                    AutomationProperties.SetName(combined, "Bold italic");
                    var emphasis = new Italic();
                    AppendMarkdownInlines(emphasis.Inlines, combinedInline.Inlines);
                    combined.Inlines.Add(emphasis);
                    inlines.Add(combined);
                    break;
                }
                case MarkdownModel.MarkdownStrongInline strongInline:
                {
                    var strong = new Bold();
                    AppendMarkdownInlines(strong.Inlines, strongInline.Inlines);
                    inlines.Add(strong);
                    break;
                }
                case MarkdownModel.MarkdownStrikethroughInline strikeInline:
                {
                    var strike = new Span { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                    AutomationProperties.SetName(strike, "Strikethrough");
                    AppendMarkdownInlines(strike.Inlines, strikeInline.Inlines);
                    inlines.Add(strike);
                    break;
                }
                case MarkdownModel.MarkdownEmphasisInline emphasisInline:
                {
                    var emphasis = new Italic();
                    AppendMarkdownInlines(emphasis.Inlines, emphasisInline.Inlines);
                    inlines.Add(emphasis);
                    break;
                }
                case MarkdownModel.MarkdownImageInline imageInline:
                    AppendImage(inlines, imageInline.Label, imageInline.Destination, imageInline.Source);
                    break;
                case MarkdownModel.MarkdownLinkInline linkInline:
                    AppendLink(inlines, linkInline.Label, linkInline.Destination, linkInline.Source);
                    break;
                case MarkdownModel.MarkdownFootnoteReferenceInline footnoteInline:
                    AddFootnoteReference(inlines, footnoteInline.Label);
                    break;
                case MarkdownModel.MarkdownHtmlInline htmlInline:
                    AppendHtml(inlines, htmlInline);
                    break;
            }
        }
    }

    private void AppendLink(InlineCollection inlines, string label, string destination, string source)
    {
        if (LocalImageResourcePolicy.TryCreateSupportedUri(destination, out var localUri, out var localPath) &&
            TryCreateGeneratedImagePreview(label, localUri, localPath, out var localPreview))
        {
            inlines.Add(new InlineUIContainer(localPreview));
        }
        else if (ExternalUriPolicy.TryCreateSupportedUri(destination, out var target))
        {
            AddLink(inlines, label, target);
        }
        else
        {
            inlines.Add(new Run(source));
        }
    }

    private void AppendImage(InlineCollection inlines, string label, string destination, string source)
    {
        if (LocalImageResourcePolicy.TryCreateSupportedUri(destination, out var localUri, out var localPath) &&
            TryCreateGeneratedImagePreview(label, localUri, localPath, out var localPreview))
        {
            inlines.Add(new InlineUIContainer(localPreview));
        }
        else if (ExternalUriPolicy.TryCreateSupportedUri(destination, out var target))
        {
            inlines.Add(new InlineUIContainer(CreateRemoteImagePreview(label, target)));
        }
        else
        {
            inlines.Add(new Run(source));
        }
    }

    private void AppendHtml(InlineCollection inlines, MarkdownModel.MarkdownHtmlInline html)
    {
        switch (html.Kind)
        {
            case MarkdownModel.MarkdownHtmlKind.Comment:
                return;
            case MarkdownModel.MarkdownHtmlKind.LineBreak:
                inlines.Add(new LineBreak());
                return;
            case MarkdownModel.MarkdownHtmlKind.Image:
                if (html.Attributes.TryGetValue("src", out var source) &&
                    ExternalUriPolicy.TryCreateSupportedUri(source, out var imageUri))
                {
                    var label = html.Attributes.TryGetValue("alt", out var alt) ? alt : imageUri.Host;
                    inlines.Add(new InlineUIContainer(CreateRemoteImagePreview(label, imageUri)));
                }
                else
                {
                    inlines.Add(new Run(html.Source));
                }
                return;
            case MarkdownModel.MarkdownHtmlKind.Strong:
            {
                var strong = new Bold();
                AppendMarkdownInlines(strong.Inlines, html.Inlines);
                inlines.Add(strong);
                return;
            }
            case MarkdownModel.MarkdownHtmlKind.Emphasis:
            {
                var emphasis = new Italic();
                AppendMarkdownInlines(emphasis.Inlines, html.Inlines);
                inlines.Add(emphasis);
                return;
            }
            case MarkdownModel.MarkdownHtmlKind.Strikethrough:
            {
                var deleted = new Span { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                AppendMarkdownInlines(deleted.Inlines, html.Inlines);
                inlines.Add(deleted);
                return;
            }
            case MarkdownModel.MarkdownHtmlKind.Code:
            {
                var code = new Run(System.Net.WebUtility.HtmlDecode(html.Content));
                code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                code.SetResourceReference(TextElement.BackgroundProperty, "SubtleBrush");
                AutomationProperties.SetName(code, "Inline HTML code");
                inlines.Add(code);
                return;
            }
            case MarkdownModel.MarkdownHtmlKind.Link:
                if (html.Attributes.TryGetValue("href", out var href) &&
                    ExternalUriPolicy.TryCreateSupportedUri(href, out var target))
                {
                    var link = new Hyperlink { NavigateUri = target, ToolTip = new ToolTip { Content = target.AbsoluteUri } };
                    AppendMarkdownInlines(link.Inlines, html.Inlines);
                    link.RequestNavigate += OnLinkRequestNavigate;
                    inlines.Add(link);
                }
                else
                {
                    inlines.Add(new Run(html.Source));
                }
                return;
            case MarkdownModel.MarkdownHtmlKind.Preformatted:
            {
                var preformatted = new Run(System.Net.WebUtility.HtmlDecode(html.Content));
                preformatted.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                AutomationProperties.SetName(preformatted, "Raw HTML preformatted text");
                inlines.Add(preformatted);
                return;
            }
            case MarkdownModel.MarkdownHtmlKind.BlockQuote:
            {
                var quote = new Italic();
                AutomationProperties.SetName(quote, "Raw HTML block quote");
                AppendMarkdownInlines(quote.Inlines, html.Inlines);
                inlines.Add(quote);
                return;
            }
            case MarkdownModel.MarkdownHtmlKind.Heading:
                inlines.Add(CreateHeading(html.HeadingLevel, html.Inlines));
                return;
            case MarkdownModel.MarkdownHtmlKind.Block:
            {
                var block = new Span();
                if (inlines.Count > 0)
                {
                    block.Inlines.Add(new LineBreak());
                }
                AppendMarkdownInlines(block.Inlines, html.Inlines);
                block.Inlines.Add(new LineBreak());
                inlines.Add(block);
                return;
            }
        }
    }

    private Span CreateHeading(int level, IReadOnlyList<MarkdownModel.MarkdownInline> content)
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
        AppendMarkdownInlines(heading.Inlines, content);
        return heading;
    }

    private Span CreateListItem(
        string prefix,
        IReadOnlyList<MarkdownModel.MarkdownInline> content,
        string automationName)
    {
        var item = new Span();
        AutomationProperties.SetName(item, automationName);
        item.Inlines.Add(new Run(prefix));
        AppendMarkdownInlines(item.Inlines, content);
        return item;
    }

    private StackPanel CreateNestedList(IReadOnlyList<MarkdownModel.MarkdownListItem> items)
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

    private StackPanel CreateDefinitionList(IReadOnlyList<MarkdownModel.MarkdownDefinitionItem> definitions)
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

    private Grid CreateTable(MarkdownModel.MarkdownTableBlock table)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };
        BindBlockWidth(grid);
        AutomationProperties.SetName(grid, "Markdown table");

        for (var column = 0; column < table.Header.Count; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rows = new List<IReadOnlyList<string>> { table.Header };
        rows.AddRange(table.Rows);
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < table.Header.Count; column++)
            {
                var cellText = CreateNestedMarkdownTextBlock(rows[row][column]);
                cellText.FontWeight = row == 0 ? FontWeights.SemiBold : FontWeight;
                cellText.TextAlignment = table.Alignments[column] switch
                {
                    MarkdownModel.MarkdownTableAlignment.Center => TextAlignment.Center,
                    MarkdownModel.MarkdownTableAlignment.Right => TextAlignment.Right,
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

}
