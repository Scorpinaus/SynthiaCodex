namespace SynthiaCode.Presentation.Markdown;

public sealed record MarkdownDocument(
    IReadOnlyList<MarkdownBlock> Blocks,
    IReadOnlyDictionary<string, string> FootnoteDefinitions);

public abstract record MarkdownBlock;

public sealed record MarkdownInlineBlock(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownHeadingBlock(
    int Level,
    IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownListItemBlock(
    string Prefix,
    string AutomationName,
    IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownNestedListBlock(
    IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

public sealed record MarkdownDefinitionListBlock(
    IReadOnlyList<MarkdownDefinitionItem> Items) : MarkdownBlock;

public sealed record MarkdownTableBlock(
    IReadOnlyList<string> Header,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<MarkdownTableAlignment> Alignments) : MarkdownBlock;

public sealed record MarkdownQuoteBlock(string Content) : MarkdownBlock;

public sealed record MarkdownCodeBlock(string Content, string InfoString) : MarkdownBlock;

public sealed record MarkdownHorizontalRuleBlock : MarkdownBlock;

public sealed record MarkdownListItem(
    int Depth,
    string Prefix,
    string AutomationName,
    bool? TaskState,
    string Content);

public sealed record MarkdownDefinitionItem(
    string Term,
    IReadOnlyList<string> Definitions);

public enum MarkdownTableAlignment
{
    Left,
    Center,
    Right
}

public abstract record MarkdownInline;

public sealed record MarkdownTextInline(string Text) : MarkdownInline;

public sealed record MarkdownCodeInline(string Text) : MarkdownInline;

public sealed record MarkdownStrongInline(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownEmphasisInline(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownStrongEmphasisInline(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownStrikethroughInline(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownLinkInline(
    string Label,
    string Destination,
    string Source) : MarkdownInline;

public sealed record MarkdownImageInline(
    string Label,
    string Destination,
    string Source,
    bool HasImageMarker) : MarkdownInline;

public sealed record MarkdownFootnoteReferenceInline(string Label) : MarkdownInline;

public sealed record MarkdownHtmlInline(
    MarkdownHtmlKind Kind,
    string Tag,
    string Source,
    string Content,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<MarkdownInline> Inlines,
    int HeadingLevel = 0) : MarkdownInline;

public enum MarkdownHtmlKind
{
    Comment,
    LineBreak,
    Image,
    Strong,
    Emphasis,
    Strikethrough,
    Code,
    Link,
    Preformatted,
    BlockQuote,
    Heading,
    Block
}
