using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Klydis.App.Helpers;

/// <summary>
/// Token types for syntax highlighting in code blocks.
/// </summary>
public enum SyntaxTokenType
{
    Text,
    Keyword,
    ControlKeyword,
    String,
    Number,
    Comment,
    Type,
    Function,
    Variable,
    Property,
    Operator
}

/// <summary>
/// A token with text and syntax category.
/// </summary>
public sealed class SyntaxToken
{
    public string Text { get; }
    public SyntaxTokenType Type { get; }

    public SyntaxToken(string text, SyntaxTokenType type)
    {
        Text = text;
        Type = type;
    }
}

/// <summary>
/// High-performance syntax highlighter for model code blocks in Klydis.
/// Supports PowerShell, C#, Python, JavaScript/TypeScript, JSON, SQL, Bash/Shell, HTML/XML/XAML,
/// CSS, Rust, Go, C/C++, Java, YAML, and generic code.
/// </summary>
public static class CodeSyntaxHighlighter
{
    // Common string & comment patterns
    private static readonly Regex GenericStringAndCommentRegex = new(
        @"(?<comment>//.*?$|/\*[\s\S]*?\*/|#.*?$|--.*?$|<!--[\s\S]*?-->)|" +
        @"(?<string>""""""[\s\S]*?""""""|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`[^`]*`)|" +
        @"(?<number>\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|" +
        @"(?<type>\[[a-zA-Z0-9_.:]+\]|\b(?:string|int|bool|double|float|long|void|object|char|byte|short|uint|ulong|decimal|var|let|const|auto|boolean|any|number|None|True|False|null|undefined|nil|true|false)\b)|" +
        @"(?<variable>\$[a-zA-Z0-9_]+|\-[a-zA-Z0-9_]+|--[a-zA-Z0-9_]+|\b(?:this|self|cls|base)\b)|" +
        @"\b(?<control>if|else|elif|for|foreach|while|do|switch|case|break|continue|return|try|catch|finally|throw|yield|await|async)\b|" +
        @"\b(?<keyword>class|struct|interface|enum|public|private|protected|internal|static|virtual|override|abstract|sealed|def|fn|function|func|import|export|from|as|in|is|new|select|where|orderby|group|join|package|namespace|using|module|extern|mut|impl|trait|type|typeof|sizeof|default|goto)\b|" +
        @"\b(?<cmd>[A-Z][a-zA-Z0-9]*-[A-Z][a-zA-Z0-9]*)\b|" +
        @"(?<func>\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\())|" +
        @"(?<other>[^\s""'`#/\-\$\d\[\]\w]+|\w+|\s+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex JsonRegex = new(
        @"(?<comment>//.*?$|/\*[\s\S]*?\*/)|" +
        @"(?<key>""(?:\\.|[^""\\])*""(?=\s*:))|" +
        @"(?<string>""(?:\\.|[^""\\])*"")|" +
        @"(?<number>\b-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|" +
        @"\b(?<keyword>true|false|null)\b|" +
        @"(?<other>[^\s""/\-\d\w]+|\w+|\s+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex XmlHtmlRegex = new(
        @"(?<comment><!--[\s\S]*?-->)|" +
        @"(?<tag></?[a-zA-Z0-9_:-]+)|" +
        @"(?<attr>[a-zA-Z0-9_:-]+(?=\s*=))|" +
        @"(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|" +
        @"(?<other>[^<""'\s]+|\s+|<|>|/|=)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Dark-theme palette (VS Code Dark+ inspired)
    private static readonly SolidColorBrush DarkKeywordBrush = Freeze(new SolidColorBrush(Color.FromRgb(86, 156, 214)));       // #569CD6
    private static readonly SolidColorBrush DarkControlBrush = Freeze(new SolidColorBrush(Color.FromRgb(197, 134, 192)));     // #C586C0
    private static readonly SolidColorBrush DarkStringBrush = Freeze(new SolidColorBrush(Color.FromRgb(206, 145, 120)));      // #CE9178
    private static readonly SolidColorBrush DarkNumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(181, 206, 168)));      // #B5CEA8
    private static readonly SolidColorBrush DarkCommentBrush = Freeze(new SolidColorBrush(Color.FromRgb(106, 153, 85)));      // #6A9955
    private static readonly SolidColorBrush DarkTypeBrush = Freeze(new SolidColorBrush(Color.FromRgb(78, 201, 176)));         // #4EC9B0
    private static readonly SolidColorBrush DarkFunctionBrush = Freeze(new SolidColorBrush(Color.FromRgb(220, 220, 170)));    // #DCDCAA
    private static readonly SolidColorBrush DarkVariableBrush = Freeze(new SolidColorBrush(Color.FromRgb(156, 220, 254)));    // #9CDCFE
    private static readonly SolidColorBrush DarkPropertyBrush = Freeze(new SolidColorBrush(Color.FromRgb(156, 220, 254)));    // #9CDCFE
    private static readonly SolidColorBrush DarkOperatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(212, 212, 212)));    // #D4D4D4
    private static readonly SolidColorBrush DarkDefaultBrush = Freeze(new SolidColorBrush(Color.FromRgb(229, 229, 229)));     // #E5E5E5

    // Light-theme palette
    private static readonly SolidColorBrush LightKeywordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 0, 255)));         // #0000FF
    private static readonly SolidColorBrush LightControlBrush = Freeze(new SolidColorBrush(Color.FromRgb(175, 0, 219)));      // #AF00DB
    private static readonly SolidColorBrush LightStringBrush = Freeze(new SolidColorBrush(Color.FromRgb(163, 21, 21)));       // #A31515
    private static readonly SolidColorBrush LightNumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(9, 134, 88)));        // #098658
    private static readonly SolidColorBrush LightCommentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 128, 0)));        // #008000
    private static readonly SolidColorBrush LightTypeBrush = Freeze(new SolidColorBrush(Color.FromRgb(38, 127, 153)));        // #267F99
    private static readonly SolidColorBrush LightFunctionBrush = Freeze(new SolidColorBrush(Color.FromRgb(121, 94, 38)));     // #795E26
    private static readonly SolidColorBrush LightVariableBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 16, 128)));      // #001080
    private static readonly SolidColorBrush LightPropertyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 16, 128)));      // #001080
    private static readonly SolidColorBrush LightOperatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(51, 51, 51)));       // #333333
    private static readonly SolidColorBrush LightDefaultBrush = Freeze(new SolidColorBrush(Color.FromRgb(31, 31, 31)));        // #1F1F1F

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Parses code into syntax tokens based on language.
    /// </summary>
    public static List<SyntaxToken> Tokenize(string code, string? language)
    {
        var tokens = new List<SyntaxToken>();
        if (string.IsNullOrEmpty(code))
        {
            return tokens;
        }

        string lang = (language ?? string.Empty).ToLowerInvariant().Trim();

        if (lang is "json" or "jsonc")
        {
            var matches = JsonRegex.Matches(code);
            foreach (Match m in matches)
            {
                if (m.Groups["comment"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Comment));
                else if (m.Groups["key"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Property));
                else if (m.Groups["string"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.String));
                else if (m.Groups["number"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Number));
                else if (m.Groups["keyword"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Keyword));
                else tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Text));
            }
            return tokens;
        }

        if (lang is "html" or "xml" or "xaml" or "svg")
        {
            var matches = XmlHtmlRegex.Matches(code);
            foreach (Match m in matches)
            {
                if (m.Groups["comment"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Comment));
                else if (m.Groups["tag"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Keyword));
                else if (m.Groups["attr"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Variable));
                else if (m.Groups["string"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.String));
                else tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Text));
            }
            return tokens;
        }

        // Generic / PowerShell / C# / Python / JS / TS / Bash / SQL / Rust / Go / etc.
        var genericMatches = GenericStringAndCommentRegex.Matches(code);
        foreach (Match m in genericMatches)
        {
            if (m.Groups["comment"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Comment));
            else if (m.Groups["string"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.String));
            else if (m.Groups["number"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Number));
            else if (m.Groups["type"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Type));
            else if (m.Groups["variable"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Variable));
            else if (m.Groups["control"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.ControlKeyword));
            else if (m.Groups["keyword"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Keyword));
            else if (m.Groups["cmd"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Function));
            else if (m.Groups["func"].Success) tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Function));
            else tokens.Add(new SyntaxToken(m.Value, SyntaxTokenType.Text));
        }

        return tokens;
    }

    /// <summary>
    /// Builds WPF Inline runs with syntax-highlighted foreground colors for the code.
    /// </summary>
    public static List<Inline> BuildInlines(string code, string? language, bool isDarkTheme = true)
    {
        var inlines = new List<Inline>();
        var tokens = Tokenize(code, language);

        foreach (var token in tokens)
        {
            var run = new Run(token.Text);
            var brush = GetBrushForToken(token.Type, isDarkTheme);
            if (brush != null)
            {
                run.Foreground = brush;
            }

            if (token.Type == SyntaxTokenType.Comment)
            {
                run.FontStyle = FontStyles.Italic;
            }
            else if (token.Type is SyntaxTokenType.Keyword or SyntaxTokenType.ControlKeyword)
            {
                run.FontWeight = FontWeights.Medium;
            }

            inlines.Add(run);
        }

        return inlines;
    }

    public static Brush GetBrushForToken(SyntaxTokenType type, bool isDarkTheme = true)
    {
        if (isDarkTheme)
        {
            return type switch
            {
                SyntaxTokenType.Keyword => DarkKeywordBrush,
                SyntaxTokenType.ControlKeyword => DarkControlBrush,
                SyntaxTokenType.String => DarkStringBrush,
                SyntaxTokenType.Number => DarkNumberBrush,
                SyntaxTokenType.Comment => DarkCommentBrush,
                SyntaxTokenType.Type => DarkTypeBrush,
                SyntaxTokenType.Function => DarkFunctionBrush,
                SyntaxTokenType.Variable => DarkVariableBrush,
                SyntaxTokenType.Property => DarkPropertyBrush,
                SyntaxTokenType.Operator => DarkOperatorBrush,
                _ => DarkDefaultBrush
            };
        }

        return type switch
        {
            SyntaxTokenType.Keyword => LightKeywordBrush,
            SyntaxTokenType.ControlKeyword => LightControlBrush,
            SyntaxTokenType.String => LightStringBrush,
            SyntaxTokenType.Number => LightNumberBrush,
            SyntaxTokenType.Comment => LightCommentBrush,
            SyntaxTokenType.Type => LightTypeBrush,
            SyntaxTokenType.Function => LightFunctionBrush,
            SyntaxTokenType.Variable => LightVariableBrush,
            SyntaxTokenType.Property => LightPropertyBrush,
            SyntaxTokenType.Operator => LightOperatorBrush,
            _ => LightDefaultBrush
        };
    }
}
