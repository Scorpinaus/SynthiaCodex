# Assistant Markdown rendering implementation plan

## Goal

Close the seven documented assistant-rendering gaps while preserving SynthiaCode's native WPF presentation, safe-link policy, streaming behavior, and literal fallback for malformed input.

## Research baseline

- CommonMark 0.31.2 defines list-item indentation, fenced-code info strings, inline raw HTML, and raw HTML blocks: <https://spec.commonmark.org/0.31.2/>.
- GitHub's Markdown guidance establishes the `[^label]` footnote convention, bottom-of-document footnote placement, remote image syntax, nested-list expectations, and language identifiers for highlighted fenced code: <https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax> and <https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/creating-and-highlighting-code-blocks>.
- Markdown Extra establishes a widely used definition-list extension in which a single-line term is followed by one or more `: definition` lines: <https://michelf.ca/specs/markdown-extra/>.
- WPF `BitmapImage` supports URI-backed images and decode-size constraints, while `Clipboard.SetText` provides Unicode clipboard output on the UI's STA thread: <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-use-a-bitmapimage> and <https://learn.microsoft.com/en-us/dotnet/api/system.windows.clipboard.settext>.

## Rendering contract

### Remote images

- Render `![alt](https://...)` and `![alt](http://...)` as bounded, aspect-preserving images.
- Reuse the existing external URI allowlist so file, script, data, and other schemes remain literal.
- Keep a safe clickable source link and accessible alt/source names.
- Use delayed URI-backed decoding with a bounded decode width; failed downloads must not crash or remove the surrounding answer.

### Raw HTML

- Render a safe native subset rather than hosting a browser: `strong`/`b`, `em`/`i`, `del`/`s`, `code`/`kbd`, `br`, `a`, `p`/`div`, headings, `blockquote`, `pre`, and `img`.
- Ignore HTML comments.
- Route `href` and `src` through the existing safe link/image policies.
- Do not execute scripts, styles, event attributes, embedded frames, forms, media, or arbitrary XAML. Unsupported, unsafe, or malformed HTML remains visible literally.

### Nested lists

- Parse contiguous ordered, unordered, and task-list rows as a hierarchy.
- Derive depth from indentation, preserve ordered markers and task state, and use real layout margins rather than space characters.
- Keep inline Markdown active inside every item and expose list/item automation names.

### Footnotes and definition lists

- Recognize `[^label]` references and `[^label]: definition` declarations.
- Remove declarations from their source position, append referenced definitions at the bottom, render references as superscript navigation, and keep missing references literal.
- Recognize Markdown Extra term/`: definition` groups, lay definitions out under their terms, and preserve inline Markdown.

### Highlighting and code copy

- Preserve the complete code text exactly.
- Normalize common fenced-code aliases and highlight comments, strings, numbers, keywords, and punctuation for common C-family, JSON, XML/HTML, shell/PowerShell, Python, SQL, and plain-text inputs.
- Unknown or absent language identifiers remain uncolored monospaced text.
- Each fenced block exposes its normalized language label and an accessible **Copy code** button that copies only that block's source.

## TDD phases

1. Add focused rendered-WPF tests for all seven features, malformed/unsafe fallbacks, accessibility, text preservation, and exact clipboard output. Run the focused group and capture the expected failures before production changes.
2. Implement the parsing and presentation changes in small units. Run the focused group after each logical unit and resolve any regression or newly exposed defect.
3. Run the complete Debug and Release behavioral suites.
4. Rebuild the solution non-incrementally in Debug and Release, verify both executable artifacts, run `git diff --check`, and update feature parity.

## Feature-parity updates

`feature_parity.md` will be updated after each completed phase:

- research/plan contract;
- red-test contract;
- implementation/focused verification;
- full verification and executable rebuild.
