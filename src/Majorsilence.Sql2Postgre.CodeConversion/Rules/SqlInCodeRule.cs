using System.Text;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using DatabaseModel = Majorsilence.Sql2Postgre.Translation.Semantics.SemanticModel;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>
/// SQL in code: the text of <c>new SqlCommand(sql, …)</c>, <c>CommandText = sql</c>, Dapper's query
/// and execute methods, and EF Core's raw SQL methods, followed back to the literal, constant or
/// once-assigned local that holds it, translated as a command (<see cref="CommandTranslator"/>)
/// and written back in the same place. EF's <c>{0}</c> placeholders and the holes of an
/// interpolated <c>FromSql</c> come back as they were.
/// </summary>
public sealed partial class SqlInCodeRule : ICodeRule
{
    private enum SiteKind { Command, Positional, Interpolated }

    private static readonly HashSet<string> CommandTypes = new(StringComparer.OrdinalIgnoreCase) { "SqlCommand", "SqlDataAdapter", "NpgsqlCommand", "NpgsqlDataAdapter" };

    private static readonly HashSet<string> DapperMethods = new(StringComparer.Ordinal)
    {
        "Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync",
        "QuerySingle", "QuerySingleAsync", "QuerySingleOrDefault", "QuerySingleOrDefaultAsync", "QueryMultiple", "QueryMultipleAsync",
        "QueryUnbufferedAsync", "Execute", "ExecuteAsync", "ExecuteScalar", "ExecuteScalarAsync", "ExecuteReader", "ExecuteReaderAsync",
    };

    private static readonly HashSet<string> EfPositional = new(StringComparer.Ordinal) { "FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "SqlQueryRaw" };

    private static readonly HashSet<string> EfInterpolated = new(StringComparer.Ordinal)
    {
        "FromSql", "FromSqlInterpolated", "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync", "SqlQuery",
    };

    /// <summary>The text of a SQL site: its value, the node to replace, and the interpolation holes standing in as markers.</summary>
    private sealed record SqlText(string Value, SyntaxNode Target, SyntaxNode? Form, IReadOnlyDictionary<string, string> Holes, bool Verbatim);

    private sealed class Unresolved(string why) : Exception(why);

    public void Apply(CodeContext context)
    {
        bool dapper = context.Imports.Contains("Dapper");
        bool ef = context.Imports.Any(i => i.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        bool ado = context.Imports.Any(AdoNetMap.IsSqlClient) || context.Imports.Contains("Npgsql");
        if (!dapper && !ef && !ado)
            return;
        var sites = context.Root switch
        {
            CSS.CompilationUnitSyntax cs => CSharpSites(cs, dapper, ef, ado),
            VBS.CompilationUnitSyntax vb => VisualBasicSites(vb, dapper, ef, ado),
            _ => [],
        };
        var done = new HashSet<TextSpanKey>();
        foreach (var (expression, kind) in sites)
            Translate(context, expression, kind, done);
    }

    private readonly record struct TextSpanKey(int Start, int Length);

    private static void Translate(CodeContext c, SyntaxNode expression, SiteKind kind, HashSet<TextSpanKey> done)
    {
        SqlText text;
        try
        {
            text = c.Root is CSS.CompilationUnitSyntax ? ResolveCSharp(expression, kind, c) : ResolveVisualBasic(expression, kind, c);
        }
        catch (Unresolved ex)
        {
            c.Report(DiagnosticCodes.SqlBuiltAtRunTime, Severity.Error, expression,
                $"SQL built at run time ({ex.Message}) cannot be translated where it is written: translate the pieces, or build it from constants.");
            return;
        }
        if (!done.Add(new TextSpanKey(text.Target.SpanStart, text.Target.Span.Length)))
            return;
        // A bare name is a stored procedure (CommandType.StoredProcedure), WP A.4's.
        if (ProcedureName().IsMatch(text.Value))
            return;

        string template = text.Value;
        if (kind == SiteKind.Positional)
            template = Positional().Replace(template, m => $"@{Marker}{m.Groups[1].Value}_");
        var model = c.Options.Model ?? DatabaseModel.Build(new SourceDatabase("", "", 160, [], [], [], [], [], []));
        var translation = CommandTranslator.Translate(template, model, c.Options.Schema, c.Path);
        foreach (var d in translation.Diagnostics)
            c.Report(d.Code, d.Severity, expression, "In SQL: " + d.Message);
        if (translation.Diagnostics.Any(d => d.Severity == Severity.Error))
            return;
        if (translation.CompatFunctions.Count > 0)
            c.Report(DiagnosticCodes.CompatHelperInCode, Severity.Warning, expression,
                $"The translated SQL calls mssql_compat helpers ({string.Join(", ", translation.CompatFunctions)}): they must exist in the database (sql2pg schema creates the ones the schema needs; create these too).");

        string sql = translation.Sql;
        if (!text.Value.Contains('\n'))
            sql = Regex.Replace(sql, @"\n\s*", " ");
        if (text.Value.TrimEnd().EndsWith(';'))
            sql += ";";
        if (kind == SiteKind.Positional)
            sql = MarkerParameter().Replace(sql, m => "{" + m.Groups[1].Value + "}");
        if (sql == text.Value)
            return;
        string replacement = text.Holes.Count > 0
            ? Interpolated(c, sql, text.Holes, text.Verbatim)
            : c.Language.StringLiteral(sql, text.Form);
        c.Edit(text.Target, replacement, "SQL translated to PostgreSQL");
    }

    private const string Marker = "__sql2pg_p";

    [GeneratedRegex(@"^\s*[\w\.\[\]""]+\s*;?\s*$")]
    private static partial Regex ProcedureName();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Positional();

    [GeneratedRegex(@"@__sql2pg_p(\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerParameter();

    /// <summary>An interpolated string with the holes back in place of their markers.</summary>
    private static string Interpolated(CodeContext c, string sql, IReadOnlyDictionary<string, string> holes, bool verbatim)
    {
        bool cs = c.Root is CSS.CompilationUnitSyntax;
        bool multiLine = sql.Contains('\n');
        var sb = new StringBuilder(cs ? (verbatim || multiLine ? "$@\"" : "$\"") : "$\"");
        int position = 0;
        foreach (Match m in MarkerParameter().Matches(sql))
        {
            sb.Append(Escape(sql[position..m.Index], cs, verbatim || multiLine));
            sb.Append('{').Append(holes[m.Value.ToLowerInvariant()]).Append('}');
            position = m.Index + m.Length;
        }
        sb.Append(Escape(sql[position..], cs, verbatim || multiLine));
        return sb.Append('"').ToString();
    }

    private static string Escape(string text, bool cs, bool verbatim)
    {
        text = text.Replace("{", "{{").Replace("}", "}}");
        if (!cs || verbatim)
            return text.Replace("\"", "\"\"");
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    // ---- C# ----

    private static List<(SyntaxNode, SiteKind)> CSharpSites(CSS.CompilationUnitSyntax root, bool dapper, bool ef, bool ado)
    {
        var sites = new List<(SyntaxNode, SiteKind)>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case CSS.ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var first, ..] } creation when ado && CommandTypes.Contains(LastName(creation.Type.ToString())):
                    sites.Add((first.Expression, SiteKind.Command));
                    break;
                case CSS.ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments: [var first, ..] } implicitCreation
                    when ado && implicitCreation.Parent is CSS.EqualsValueClauseSyntax { Parent: CSS.VariableDeclaratorSyntax { Parent: CSS.VariableDeclarationSyntax d } }
                    && CommandTypes.Contains(LastName(d.Type.ToString())):
                    sites.Add((first.Expression, SiteKind.Command));
                    break;
                case CSS.AssignmentExpressionSyntax { Left: var left, Right: var right } when ado && left is CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText" }
                    or CSS.IdentifierNameSyntax { Identifier.ValueText: "CommandText", Parent.Parent: CSS.InitializerExpressionSyntax }:
                    sites.Add((right, SiteKind.Command));
                    break;
                case CSS.InvocationExpressionSyntax { Expression: CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method }, ArgumentList.Arguments.Count: > 0 } call:
                    var argument = call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "sql") ?? call.ArgumentList.Arguments[0];
                    if (dapper && DapperMethods.Contains(method))
                        sites.Add((argument.Expression, SiteKind.Command));
                    else if (ef && EfPositional.Contains(method))
                        sites.Add((argument.Expression, SiteKind.Positional));
                    else if (ef && EfInterpolated.Contains(method))
                        sites.Add((argument.Expression, SiteKind.Interpolated));
                    break;
            }
        }
        return sites;
    }

    private static string LastName(string type) => type.Split('.')[^1];

    private static SqlText ResolveCSharp(SyntaxNode expression, SiteKind kind, CodeContext c)
    {
        switch (expression)
        {
            case CSS.LiteralExpressionSyntax literal when literal.IsKind(CS.SyntaxKind.StringLiteralExpression):
                return new SqlText(literal.Token.ValueText, literal, literal, new Dictionary<string, string>(), literal.Token.Text.StartsWith('@'));
            case CSS.ParenthesizedExpressionSyntax p:
                return ResolveCSharp(p.Expression, kind, c);
            case CSS.BinaryExpressionSyntax b when b.IsKind(CS.SyntaxKind.AddExpression):
                var (l, r) = (ResolveCSharp(b.Left, kind, c), ResolveCSharp(b.Right, kind, c));
                if (l.Holes.Count > 0 || r.Holes.Count > 0)
                    throw new Unresolved("an interpolated string joined to another");
                return new SqlText(l.Value + r.Value, b, null, new Dictionary<string, string>(), false);
            case CSS.InterpolatedStringExpressionSyntax interpolated:
                return CSharpInterpolated(interpolated, kind);
            case CSS.IdentifierNameSyntax or CSS.MemberAccessExpressionSyntax:
                string name = expression is CSS.MemberAccessExpressionSyntax ma ? ma.Name.Identifier.ValueText : ((CSS.IdentifierNameSyntax)expression).Identifier.ValueText;
                return CSharpDeclaration(name, expression, kind, c);
            default:
                throw new Unresolved($"'{Short(expression)}'");
        }
    }

    private static SqlText CSharpInterpolated(CSS.InterpolatedStringExpressionSyntax interpolated, SiteKind kind)
    {
        var sb = new StringBuilder();
        var holes = new Dictionary<string, string>();
        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case CSS.InterpolatedStringTextSyntax t:
                    sb.Append(t.TextToken.ValueText);
                    break;
                case CSS.InterpolationSyntax hole when kind == SiteKind.Interpolated:
                    string marker = $"@{Marker}{holes.Count}_";
                    holes[marker] = hole.Expression.ToString() + hole.AlignmentClause?.ToString() + hole.FormatClause?.ToString();
                    sb.Append(marker);
                    break;
                default:
                    throw new Unresolved("an interpolated string with values in it");
            }
        }
        return new SqlText(sb.ToString(), interpolated, null, holes, interpolated.StringStartToken.Text.Contains('@'));
    }

    /// <summary>A const or static readonly field, or a local assigned only where it is declared, holding the SQL.</summary>
    private static SqlText CSharpDeclaration(string name, SyntaxNode use, SiteKind kind, CodeContext c)
    {
        var declarator = c.Root.DescendantNodes().OfType<CSS.VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name && v.Initializer is not null)
            .OrderBy(v => v.SpanStart <= use.SpanStart ? use.SpanStart - v.SpanStart : int.MaxValue)
            .FirstOrDefault() ?? throw new Unresolved($"'{name}', not declared with a value in this file");
        bool field = declarator.Parent?.Parent is CSS.FieldDeclarationSyntax f
                     && (f.Modifiers.Any(CS.SyntaxKind.ConstKeyword) || f.Modifiers.Any(CS.SyntaxKind.ReadOnlyKeyword));
        bool local = declarator.Parent?.Parent is CSS.LocalDeclarationStatementSyntax localDeclaration
                     && !localDeclaration.Parent!.DescendantNodes().OfType<CSS.AssignmentExpressionSyntax>()
                         .Any(a => a.Left is CSS.IdentifierNameSyntax id && id.Identifier.ValueText == name);
        if (!field && !local)
            throw new Unresolved($"'{name}', which is assigned more than once or is not read-only");
        return ResolveCSharp(declarator.Initializer!.Value, kind, c);
    }

    // ---- VB ----

    private static List<(SyntaxNode, SiteKind)> VisualBasicSites(VBS.CompilationUnitSyntax root, bool dapper, bool ef, bool ado)
    {
        var sites = new List<(SyntaxNode, SiteKind)>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VBS.ObjectCreationExpressionSyntax { ArgumentList.Arguments: [VBS.SimpleArgumentSyntax first, ..] } creation
                    when ado && CommandTypes.Contains(LastName(creation.Type.ToString())):
                    sites.Add((first.Expression, SiteKind.Command));
                    break;
                case VBS.AssignmentStatementSyntax { Left: VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var target }, Right: var right }
                    when ado && target.Equals("CommandText", StringComparison.OrdinalIgnoreCase):
                    sites.Add((right, SiteKind.Command));
                    break;
                case VBS.NamedFieldInitializerSyntax { Name.Identifier.ValueText: var field, Expression: var value }
                    when ado && field.Equals("CommandText", StringComparison.OrdinalIgnoreCase):
                    sites.Add((value, SiteKind.Command));
                    break;
                case VBS.InvocationExpressionSyntax { Expression: VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method }, ArgumentList.Arguments.Count: > 0 } call:
                    var argument = call.ArgumentList.Arguments.OfType<VBS.SimpleArgumentSyntax>()
                        .FirstOrDefault(a => a.NameColonEquals?.Name.Identifier.ValueText.Equals("sql", StringComparison.OrdinalIgnoreCase) == true)
                        ?? call.ArgumentList.Arguments[0] as VBS.SimpleArgumentSyntax;
                    if (argument is null)
                        break;
                    string m = DapperMethods.Concat(EfPositional).Concat(EfInterpolated).FirstOrDefault(x => x.Equals(method, StringComparison.OrdinalIgnoreCase)) ?? method;
                    if (dapper && DapperMethods.Contains(m))
                        sites.Add((argument.Expression, SiteKind.Command));
                    else if (ef && EfPositional.Contains(m))
                        sites.Add((argument.Expression, SiteKind.Positional));
                    else if (ef && EfInterpolated.Contains(m))
                        sites.Add((argument.Expression, SiteKind.Interpolated));
                    break;
            }
        }
        return sites;
    }

    private static SqlText ResolveVisualBasic(SyntaxNode expression, SiteKind kind, CodeContext c)
    {
        switch (expression)
        {
            case VBS.LiteralExpressionSyntax literal when literal.IsKind(VB.SyntaxKind.StringLiteralExpression):
                return new SqlText(literal.Token.ValueText, literal, literal, new Dictionary<string, string>(), false);
            case VBS.ParenthesizedExpressionSyntax p:
                return ResolveVisualBasic(p.Expression, kind, c);
            case VBS.BinaryExpressionSyntax b when b.IsKind(VB.SyntaxKind.ConcatenateExpression) || b.IsKind(VB.SyntaxKind.AddExpression):
                var (l, r) = (ResolveVisualBasic(b.Left, kind, c), ResolveVisualBasic(b.Right, kind, c));
                if (l.Holes.Count > 0 || r.Holes.Count > 0)
                    throw new Unresolved("an interpolated string joined to another");
                return new SqlText(l.Value + r.Value, b, null, new Dictionary<string, string>(), false);
            case VBS.InterpolatedStringExpressionSyntax interpolated:
                var sb = new StringBuilder();
                var holes = new Dictionary<string, string>();
                foreach (var content in interpolated.Contents)
                {
                    switch (content)
                    {
                        case VBS.InterpolatedStringTextSyntax t:
                            sb.Append(t.TextToken.ValueText);
                            break;
                        case VBS.InterpolationSyntax hole when kind == SiteKind.Interpolated:
                            string marker = $"@{Marker}{holes.Count}_";
                            holes[marker] = hole.Expression.ToString();
                            sb.Append(marker);
                            break;
                        default:
                            throw new Unresolved("an interpolated string with values in it");
                    }
                }
                return new SqlText(sb.ToString(), interpolated, null, holes, false);
            case VBS.IdentifierNameSyntax or VBS.MemberAccessExpressionSyntax:
                string name = expression is VBS.MemberAccessExpressionSyntax ma ? ma.Name.Identifier.ValueText : ((VBS.IdentifierNameSyntax)expression).Identifier.ValueText;
                var declarator = c.Root.DescendantNodes().OfType<VBS.VariableDeclaratorSyntax>()
                    .Where(v => v.Initializer is not null && v.Names.Any(n => n.Identifier.ValueText.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(v => v.SpanStart <= expression.SpanStart ? expression.SpanStart - v.SpanStart : int.MaxValue)
                    .FirstOrDefault() ?? throw new Unresolved($"'{name}', not declared with a value in this file");
                bool field = declarator.Parent is VBS.FieldDeclarationSyntax f
                             && f.Modifiers.Any(m => m.IsKind(VB.SyntaxKind.ConstKeyword) || m.IsKind(VB.SyntaxKind.ReadOnlyKeyword));
                bool local = declarator.Parent is VBS.LocalDeclarationStatementSyntax localDeclaration
                             && !localDeclaration.Parent!.DescendantNodes().OfType<VBS.AssignmentStatementSyntax>()
                                 .Any(a => a.Left is VBS.IdentifierNameSyntax id && id.Identifier.ValueText.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!field && !local)
                    throw new Unresolved($"'{name}', which is assigned more than once or is not read-only");
                return ResolveVisualBasic(declarator.Initializer!.Value, kind, c);
            default:
                throw new Unresolved($"'{Short(expression)}'");
        }
    }

    private static string Short(SyntaxNode node)
    {
        string text = node.ToString().ReplaceLineEndings(" ");
        return text.Length > 60 ? text[..57] + "…" : text;
    }
}
