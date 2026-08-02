using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SynthiaCode.App.Controls;

internal static class MarkdownSyntaxHighlighter
{
    private static readonly HashSet<string> EmptyKeywords = new(StringComparer.Ordinal);
    private static readonly HashSet<string> CSharpKeywords = CreateKeywords(
        "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while async await dynamic get init required set value when where with yield");
    private static readonly HashSet<string> JavaScriptKeywords = CreateKeywords(
        "async await break case catch class const continue debugger default delete do else export extends false finally for function if import in instanceof let new null of return static super switch this throw true try typeof undefined var void while with yield");
    private static readonly HashSet<string> CommonCKeywords = CreateKeywords(
        "break case catch class const continue default defer do else enum false finally float for func if import in int interface long match namespace new null package private protected public return short static struct switch this throw true try type uint using var void while");
    private static readonly HashSet<string> JsonKeywords = CreateKeywords("true false null");
    private static readonly HashSet<string> PythonKeywords = CreateKeywords(
        "and as assert async await break class continue def del elif else except false finally for from global if import in is lambda none nonlocal not or pass raise return true try while with yield");
    private static readonly HashSet<string> PowerShellKeywords = CreateKeywords(
        "begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while");
    private static readonly HashSet<string> ShellKeywords = CreateKeywords(
        "case do done elif else esac export fi for function if in local readonly return select then time until while");
    private static readonly HashSet<string> SqlKeywords = CreateKeywords(
        "add alter and as asc begin between by case create database default delete desc distinct drop else end exists from full group having in index inner insert into is join left like limit not null on or order outer primary procedure right row select set table then top truncate union unique update values view when where");
    private static readonly IReadOnlyDictionary<string, LanguageDefinition> Languages =
        new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new("C#", LanguageFamily.CFamily, CSharpKeywords),
            ["cs"] = new("C#", LanguageFamily.CFamily, CSharpKeywords),
            ["dotnet"] = new("C#", LanguageFamily.CFamily, CSharpKeywords),
            ["javascript"] = new("JavaScript", LanguageFamily.CFamily, JavaScriptKeywords),
            ["js"] = new("JavaScript", LanguageFamily.CFamily, JavaScriptKeywords),
            ["typescript"] = new("TypeScript", LanguageFamily.CFamily, JavaScriptKeywords),
            ["ts"] = new("TypeScript", LanguageFamily.CFamily, JavaScriptKeywords),
            ["java"] = new("Java", LanguageFamily.CFamily, CommonCKeywords),
            ["cpp"] = new("C++", LanguageFamily.CFamily, CommonCKeywords),
            ["c++"] = new("C++", LanguageFamily.CFamily, CommonCKeywords),
            ["c"] = new("C", LanguageFamily.CFamily, CommonCKeywords),
            ["go"] = new("Go", LanguageFamily.CFamily, CommonCKeywords),
            ["rust"] = new("Rust", LanguageFamily.CFamily, CommonCKeywords),
            ["json"] = new("JSON", LanguageFamily.Json, JsonKeywords),
            ["xml"] = new("XML", LanguageFamily.Markup, EmptyKeywords),
            ["html"] = new("HTML", LanguageFamily.Markup, EmptyKeywords),
            ["xaml"] = new("XAML", LanguageFamily.Markup, EmptyKeywords),
            ["python"] = new("Python", LanguageFamily.HashComment, PythonKeywords),
            ["py"] = new("Python", LanguageFamily.HashComment, PythonKeywords),
            ["powershell"] = new("PowerShell", LanguageFamily.HashComment, PowerShellKeywords),
            ["ps1"] = new("PowerShell", LanguageFamily.HashComment, PowerShellKeywords),
            ["bash"] = new("Shell", LanguageFamily.HashComment, ShellKeywords),
            ["sh"] = new("Shell", LanguageFamily.HashComment, ShellKeywords),
            ["shell"] = new("Shell", LanguageFamily.HashComment, ShellKeywords),
            ["zsh"] = new("Shell", LanguageFamily.HashComment, ShellKeywords),
            ["sql"] = new("SQL", LanguageFamily.Sql, SqlKeywords)
        };

    public static bool TryResolve(string? infoString, out string label, out LanguageDefinition definition)
    {
        label = string.Empty;
        definition = null!;
        var identifier = FirstInfoWord(infoString);
        if (identifier.Length == 0 || !Languages.TryGetValue(identifier, out var resolved))
        {
            return false;
        }

        definition = resolved;
        label = definition.Label;
        return true;
    }

    public static string DisplayLabel(string? infoString)
    {
        if (TryResolve(infoString, out var label, out _))
        {
            return label;
        }

        var identifier = FirstInfoWord(infoString);
        return identifier.Length == 0 ? "Code" : identifier;
    }

    public static void Highlight(TextBlock target, string code, LanguageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(definition);

        target.Text = string.Empty;
        for (var index = 0; index < code.Length;)
        {
            if (TryReadComment(code, index, definition.Family, out var commentEnd))
            {
                AddToken(target, code[index..commentEnd], "Syntax comment", "TextTertiaryBrush");
                index = commentEnd;
                continue;
            }

            if (TryReadString(code, index, definition.Family, out var stringEnd))
            {
                AddToken(target, code[index..stringEnd], "Syntax string", "SuccessBrush");
                index = stringEnd;
                continue;
            }

            if (char.IsDigit(code[index]) && TryReadNumber(code, index, out var numberEnd))
            {
                AddToken(target, code[index..numberEnd], "Syntax number", "WarningBrush");
                index = numberEnd;
                continue;
            }

            if (IsIdentifierStart(code[index]))
            {
                var identifierEnd = index + 1;
                while (identifierEnd < code.Length && IsIdentifierPart(code[identifierEnd]))
                {
                    identifierEnd++;
                }

                var identifier = code[index..identifierEnd];
                if (definition.Keywords.Contains(identifier))
                {
                    AddToken(target, identifier, "Syntax keyword", "InfoBrush");
                }
                else
                {
                    target.Inlines.Add(new Run(identifier));
                }

                index = identifierEnd;
                continue;
            }

            var plainEnd = index + 1;
            while (plainEnd < code.Length &&
                   !IsTokenCandidate(code, plainEnd, definition.Family))
            {
                plainEnd++;
            }
            target.Inlines.Add(new Run(code[index..plainEnd]));
            index = plainEnd;
        }
    }

    private static bool IsTokenCandidate(string code, int index, LanguageFamily family) =>
        char.IsDigit(code[index]) ||
        IsIdentifierStart(code[index]) ||
        IsStringDelimiter(code[index], family) ||
        IsCommentStart(code, index, family);

    private static bool TryReadComment(string code, int start, LanguageFamily family, out int end)
    {
        end = start;
        if (family == LanguageFamily.Markup &&
            code.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            var closing = code.IndexOf("-->", start + 4, StringComparison.Ordinal);
            end = closing < 0 ? code.Length : closing + 3;
            return true;
        }

        if (family == LanguageFamily.CFamily &&
            code.AsSpan(start).StartsWith("//", StringComparison.Ordinal))
        {
            end = FindLineEnd(code, start + 2);
            return true;
        }

        if (family == LanguageFamily.CFamily &&
            code.AsSpan(start).StartsWith("/*", StringComparison.Ordinal))
        {
            var closing = code.IndexOf("*/", start + 2, StringComparison.Ordinal);
            end = closing < 0 ? code.Length : closing + 2;
            return true;
        }

        if (family == LanguageFamily.HashComment && code[start] == '#')
        {
            end = FindLineEnd(code, start + 1);
            return true;
        }

        if (family == LanguageFamily.Sql &&
            code.AsSpan(start).StartsWith("--", StringComparison.Ordinal))
        {
            end = FindLineEnd(code, start + 2);
            return true;
        }

        return false;
    }

    private static bool TryReadString(string code, int start, LanguageFamily family, out int end)
    {
        end = start;
        var delimiter = code[start];
        if (!IsStringDelimiter(delimiter, family))
        {
            return false;
        }

        for (var index = start + 1; index < code.Length; index++)
        {
            if (code[index] == '\\')
            {
                index++;
                continue;
            }

            if (code[index] == delimiter)
            {
                end = index + 1;
                return true;
            }
        }

        end = code.Length;
        return true;
    }

    private static bool TryReadNumber(string code, int start, out int end)
    {
        end = start + 1;
        while (end < code.Length &&
               (char.IsLetterOrDigit(code[end]) || code[end] is '.' or '_' or '+' or '-'))
        {
            end++;
        }

        return true;
    }

    private static bool IsCommentStart(string code, int index, LanguageFamily family) =>
        (family == LanguageFamily.Markup && code.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal)) ||
        (family == LanguageFamily.CFamily &&
         (code.AsSpan(index).StartsWith("//", StringComparison.Ordinal) ||
          code.AsSpan(index).StartsWith("/*", StringComparison.Ordinal))) ||
        (family == LanguageFamily.HashComment && code[index] == '#') ||
        (family == LanguageFamily.Sql && code.AsSpan(index).StartsWith("--", StringComparison.Ordinal));

    private static bool IsStringDelimiter(char character, LanguageFamily family) =>
        character is '"' or '\'' ||
        (family is LanguageFamily.CFamily or LanguageFamily.HashComment && character == '`');

    private static int FindLineEnd(string code, int start)
    {
        var newline = code.IndexOf('\n', start);
        return newline < 0 ? code.Length : newline;
    }

    private static bool IsIdentifierStart(char character) =>
        char.IsLetter(character) || character is '_' or '$';

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '$';

    private static string FirstInfoWord(string? infoString)
    {
        if (string.IsNullOrWhiteSpace(infoString))
        {
            return string.Empty;
        }

        var trimmed = infoString.Trim();
        var separator = trimmed.IndexOfAny([' ', '\t', '{']);
        return (separator < 0 ? trimmed : trimmed[..separator]).Trim().ToLowerInvariant();
    }

    private static HashSet<string> CreateKeywords(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static void AddToken(TextBlock target, string text, string automationName, string brushResource)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, brushResource);
        AutomationProperties.SetName(run, automationName);
        target.Inlines.Add(run);
    }

    internal sealed record LanguageDefinition(
        string Label,
        LanguageFamily Family,
        IReadOnlySet<string> Keywords);

    internal enum LanguageFamily
    {
        CFamily,
        HashComment,
        Json,
        Markup,
        Sql
    }
}
