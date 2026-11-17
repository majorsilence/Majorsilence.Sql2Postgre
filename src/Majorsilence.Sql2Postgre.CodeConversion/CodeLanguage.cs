using System.Text;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Majorsilence.Sql2Postgre.CodeConversion;

/// <summary>What the converter needs to know about one source language.</summary>
public interface ICodeLanguage
{
    /// <summary><c>C#</c> or <c>VB</c>.</summary>
    string Name { get; }

    SyntaxTree Parse(string text, string path);

    /// <summary>The value of a plain string literal (not interpolated), or null when the node is not one.</summary>
    string? StringValue(SyntaxNode node);

    /// <summary>
    /// A string literal holding <paramref name="value"/>, in the form of <paramref name="original"/>
    /// where that can hold it (verbatim stays verbatim, raw stays raw at the same indentation).
    /// </summary>
    string StringLiteral(string value, SyntaxNode? original = null);

    /// <summary>The directive importing a namespace (<c>using Npgsql;</c>, <c>Imports Npgsql</c>).</summary>
    string Import(string ns);

    /// <summary>The namespaces the file imports.</summary>
    IReadOnlyList<string> Imports(SyntaxNode root);
}

public static class CodeLanguages
{
    public static readonly ICodeLanguage CSharp = new CSharpLanguage();
    public static readonly ICodeLanguage VisualBasic = new VisualBasicLanguage();

    /// <summary>The language of a file by its extension, or null when it is neither C# nor VB.</summary>
    public static ICodeLanguage? For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => CSharp,
        ".vb" => VisualBasic,
        _ => null,
    };
}

internal sealed class CSharpLanguage : ICodeLanguage
{
    private static readonly CS.CSharpParseOptions Options = new(CS.LanguageVersion.Preview);

    public string Name => "C#";

    public SyntaxTree Parse(string text, string path) => CS.CSharpSyntaxTree.ParseText(text, Options, path);

    public string? StringValue(SyntaxNode node) =>
        node is CSS.LiteralExpressionSyntax literal && literal.IsKind(CS.SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    public string StringLiteral(string value, SyntaxNode? original = null)
    {
        var token = (original as CSS.LiteralExpressionSyntax)?.Token;
        if (token is { } t && (t.IsKind(CS.SyntaxKind.MultiLineRawStringLiteralToken) || t.IsKind(CS.SyntaxKind.SingleLineRawStringLiteralToken)))
            return Raw(value, t);
        if (token is { } v && v.Text.StartsWith('@') || token is null && value.Contains('\n'))
            return "@\"" + value.Replace("\"", "\"\"") + "\"";
        return Regular(value);
    }

    private static string Regular(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A raw literal with enough quotes, indented like the original's closing quotes.</summary>
    private static string Raw(string value, SyntaxToken original)
    {
        int longest = 0, run = 0;
        foreach (char c in value)
        {
            run = c == '"' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string quotes = new('"', Math.Max(3, longest + 1));
        string text = original.Text;
        int lastNewLine = text.LastIndexOf('\n');
        string indent = lastNewLine < 0 ? "" : new string(text[(lastNewLine + 1)..].TakeWhile(char.IsWhiteSpace).ToArray());
        if (!value.Contains('\n') && original.IsKind(CS.SyntaxKind.SingleLineRawStringLiteralToken) && !value.StartsWith('"') && !value.EndsWith('"'))
            return quotes + value + quotes;
        var lines = value.Replace("\r\n", "\n").Split('\n');
        return quotes + "\n" + string.Concat(lines.Select(l => (l.Length == 0 ? "" : indent + l) + "\n")) + indent + quotes;
    }

    public string Import(string ns) => $"using {ns};";

    public IReadOnlyList<string> Imports(SyntaxNode root) =>
        root.DescendantNodes().OfType<CSS.UsingDirectiveSyntax>().Where(u => u.Alias is null && u.Name is not null)
            .Select(u => u.Name!.ToString()).ToList();
}

internal sealed class VisualBasicLanguage : ICodeLanguage
{
    private static readonly VB.VisualBasicParseOptions Options = new(VB.LanguageVersion.Latest);

    public string Name => "VB";

    public SyntaxTree Parse(string text, string path) => VB.VisualBasicSyntaxTree.ParseText(text, Options, path);

    public string? StringValue(SyntaxNode node) =>
        node is VBS.LiteralExpressionSyntax literal && literal.IsKind(VB.SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    /// <summary>VB strings double their quotes and may span lines (VB 14).</summary>
    public string StringLiteral(string value, SyntaxNode? original = null) => "\"" + value.Replace("\"", "\"\"") + "\"";

    public string Import(string ns) => $"Imports {ns}";

    public IReadOnlyList<string> Imports(SyntaxNode root) =>
        root.DescendantNodes().OfType<VBS.SimpleImportsClauseSyntax>().Where(i => i.Alias is null)
            .Select(i => i.Name.ToString()).ToList();
}
