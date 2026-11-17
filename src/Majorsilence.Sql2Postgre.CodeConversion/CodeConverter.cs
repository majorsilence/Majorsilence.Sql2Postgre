using Majorsilence.Sql2Postgre.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Majorsilence.Sql2Postgre.Model.Diagnostic;
using DatabaseModel = Majorsilence.Sql2Postgre.Translation.Semantics.SemanticModel;

namespace Majorsilence.Sql2Postgre.CodeConversion;

/// <summary>How code is converted.</summary>
/// <param name="Schema">The schema conversion options the database was (or will be) converted with: names, types, target version.</param>
/// <param name="Model">The source database, when <c>--source</c> is given: tables, identity columns, procedures.</param>
public sealed record CodeConversionOptions(ConversionOptions Schema, DatabaseModel? Model = null);

/// <summary>One file being converted: its tree, and the edits and diagnostics the rules add.</summary>
public sealed class CodeContext(string path, ICodeLanguage language, SyntaxTree tree, CodeConversionOptions options)
{
    public string Path { get; } = path;
    public ICodeLanguage Language { get; } = language;
    public SyntaxTree Tree { get; } = tree;
    public SyntaxNode Root { get; } = tree.GetRoot();
    public SourceText Text { get; } = tree.GetText();
    public CodeConversionOptions Options { get; } = options;
    public List<CodeEdit> Edits { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>The namespaces the file imports.</summary>
    public IReadOnlyList<string> Imports => _imports ??= Language.Imports(Root);
    private IReadOnlyList<string>? _imports;

    public void Edit(TextSpan span, string replacement, string reason) => Edits.Add(new CodeEdit(span, replacement, reason));

    public void Edit(SyntaxNode node, string replacement, string reason) => Edit(node.Span, replacement, reason);

    public void Edit(SyntaxToken token, string replacement, string reason) => Edit(token.Span, replacement, reason);

    /// <summary>A diagnostic at a node, with its line and column in the file.</summary>
    public void Report(string code, Severity severity, SyntaxNodeOrToken at, string message)
    {
        var position = Tree.GetLineSpan(at.Span).StartLinePosition;
        Diagnostics.Add(new Diagnostic(code, severity, new ObjectName("", Path), message,
            new SourceSpan(position.Line + 1, position.Character + 1, at.Span.Length)));
    }
}

/// <summary>A conversion rule: finds what it changes in a file and adds edits and diagnostics.</summary>
public interface ICodeRule
{
    void Apply(CodeContext context);
}

/// <summary>
/// Converts C# and VB.NET source files. Each rule adds edits (span, replacement) and
/// diagnostics; the edits are applied to the original text, so everything else in the file —
/// formatting, comments, line endings — stays as it was.
/// </summary>
public sealed class CodeConverter(CodeConversionOptions options, IReadOnlyList<ICodeRule>? rules = null)
{
    private readonly IReadOnlyList<ICodeRule> _rules = rules ?? DefaultRules();

    /// <summary>The rules, in the order they run.</summary>
    public static IReadOnlyList<ICodeRule> DefaultRules() => [new Rules.AdoNetRule(), new Rules.SqlInCodeRule()];

    public CodeConversionResult Convert(IEnumerable<(string RelativePath, string Text)> files) =>
        new(files.Select(f => ConvertFile(f.RelativePath, f.Text)).ToList());

    public CodeFileResult ConvertFile(string relativePath, string text)
    {
        var language = CodeLanguages.For(relativePath)
                       ?? throw new ArgumentException($"{relativePath} is not a .cs or .vb file.", nameof(relativePath));
        var context = new CodeContext(relativePath, language, language.Parse(text, relativePath), options);
        try
        {
            foreach (var rule in _rules)
                rule.Apply(context);
            string converted = EditEngine.Apply(text, context.Edits);
            return new CodeFileResult(relativePath, language.Name, converted, context.Edits.ToList(), context.Diagnostics.ToList());
        }
        catch (Exception ex) when (ex is OverlappingEditsException or InvalidOperationException or ArgumentException)
        {
            context.Diagnostics.Add(new Diagnostic(DiagnosticCodes.InternalError, Severity.Error, new ObjectName("", relativePath),
                $"The converter failed on this file and left it unchanged: {ex.Message}"));
            return new CodeFileResult(relativePath, language.Name, text, [], context.Diagnostics.ToList());
        }
    }
}
