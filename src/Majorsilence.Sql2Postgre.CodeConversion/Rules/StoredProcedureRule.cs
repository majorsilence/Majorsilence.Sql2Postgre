using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>
/// Stored procedure calls (<c>CommandType.StoredProcedure</c> in ADO.NET, <c>commandType:</c> in
/// Dapper). The procedure's name becomes its converted name. With the schema:
/// <list type="bullet">
/// <item>a procedure converted to a function is read with <c>SELECT * FROM f(name =&gt; @name, …)</c> and <c>CommandType.Text</c>;</item>
/// <item>a procedure stays a stored procedure (Npgsql issues CALL), and its Output parameters become InputOutput (PostgreSQL's INOUT);</item>
/// <item>a procedure returning refcursors is reported (S2P8020).</item>
/// </list>
/// </summary>
public sealed class StoredProcedureRule : ICodeRule
{
    /// <summary>A stored procedure call found in the code.</summary>
    /// <param name="Name">The string literal holding the procedure's name.</param>
    /// <param name="Markers">The <c>CommandType.StoredProcedure</c> expressions.</param>
    /// <param name="Scope">The block the call is set up in (where its parameters are added).</param>
    /// <param name="Parameters">The parameters the code passes; null when they cannot be seen.</param>
    private sealed record Site(SyntaxNode Name, string Value, IReadOnlyList<SyntaxNode> Markers, SyntaxNode Scope, IReadOnlyList<Passed>? Parameters);

    private enum PassedKind { Literal, Member, Implicit }

    /// <summary>
    /// A parameter the code passes, and the node that names it: a string literal (<c>"@id"</c>), an
    /// anonymous object's member name (<c>new { id = x }</c>), or an implicitly named member
    /// (<c>new { id }</c>). Npgsql names the arguments of a CALL after the parameters exactly
    /// (quoted), so they are renamed to the converted procedure's parameter names.
    /// </summary>
    private sealed record Passed(string Name, SyntaxNode Node, PassedKind Kind);

    public void Apply(CodeContext context)
    {
        var sites = context.Root switch
        {
            CSS.CompilationUnitSyntax cs => CSharp.Sites(cs, context),
            VBS.CompilationUnitSyntax vb => VisualBasic.Sites(vb, context),
            _ => [],
        };
        if (sites.Count == 0)
            return;
        var procedures = new Procedures(context.Options);
        var seenDirections = new HashSet<int>();
        foreach (var site in sites)
        {
            if (procedures.Resolve(site.Value) is not { } target)
                continue;
            switch (target.Shape)
            {
                case null:
                    Name(context, site, target.Name.Qualified);
                    context.Report(DiagnosticCodes.ProcedureShapeUnknown, Severity.Warning, site.Name,
                        $"{site.Value} is called as a stored procedure; without --source (or not in it) the converter does not know whether it became a PostgreSQL function (read it with SELECT * FROM {target.Name.Qualified}(…)) or a procedure (CALL). Only its name is converted.");
                    break;
                case { IsFunction: true } shape:
                    var given = site.Parameters?.Select(p => p.Name).ToList() ?? shape.Parameters.Where(p => p.Default is null).Select(p => p.SourceName).ToList();
                    if (site.Parameters is null)
                        context.Report(DiagnosticCodes.ProcedureShapeUnknown, Severity.Warning, site.Name,
                            $"The parameters passed to {site.Value} cannot be seen here: the call names the ones without defaults ({string.Join(", ", given)}); check it matches what the code adds.");
                    var args = given.Select(n => shape.Parameters.FirstOrDefault(p => p.SourceName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                        .OfType<Translation.Sql.ProcedureParameterShape>()
                        .Select(p => $"{NameMapper.Quote(p.PgName)} => @{p.PgName}");
                    Name(context, site, $"SELECT * FROM {target.Name.Qualified}({string.Join(", ", args)})");
                    foreach (var marker in site.Markers)
                        context.Edit(marker, "CommandType.Text", "a procedure converted to a function is read with SELECT");
                    RenameParameters(context, site, shape);
                    break;
                case { Cursors.Count: > 0 } shape:
                    Name(context, site, target.Name.Qualified);
                    context.Report(DiagnosticCodes.ProcedureCursors, Severity.Error, site.Name,
                        $"{site.Value} returns {shape.Cursors.Count} result set(s) through refcursor parameters ({string.Join(", ", shape.Cursors)}): call it in a transaction, then FETCH ALL FROM each cursor (NpgsqlDataReader per FETCH).");
                    break;
                default:
                    Name(context, site, target.Name.Qualified);
                    RenameParameters(context, site, target.Shape);
                    if (site.Parameters is null)
                        context.Report(DiagnosticCodes.ProcedureShapeUnknown, Severity.Warning, site.Name,
                            $"The parameters passed to {site.Value} cannot be seen here: Npgsql names CALL arguments after the parameters exactly, so they must be named as the converted procedure's ({string.Join(", ", target.Shape.Parameters.Select(p => p.PgName))}).");
                    foreach (var returnValue in Directions(context, site.Scope, "ReturnValue"))
                        if (seenDirections.Add(returnValue.SpanStart))
                            context.Report(DiagnosticCodes.ProcedureShapeUnknown, Severity.Warning, returnValue,
                                $"A ReturnValue parameter: the converted procedure returns its status code in the INOUT parameter {target.Shape.ReturnCode ?? "return_code (it has none: it returned 0)"}; name the parameter that and make it InputOutput.");
                    break;
            }
        }
    }

    private static void Name(CodeContext c, Site site, string text) =>
        c.Edit(site.Name, c.Language.StringLiteral(text, site.Name), "stored procedure call");

    /// <summary>The parameters the code passes, renamed to the converted routine's parameter names.</summary>
    private static void RenameParameters(CodeContext c, Site site, Translation.Sql.ProcedureShape shape)
    {
        bool cs = c.Root is CSS.CompilationUnitSyntax;
        foreach (var passed in site.Parameters ?? [])
        {
            if (shape.Parameters.FirstOrDefault(p => p.SourceName.Equals(passed.Name, StringComparison.OrdinalIgnoreCase)) is not { } parameter
                || parameter.PgName == passed.Name)
                continue;
            string pg = parameter.PgName;
            string identifier = cs ? (CS.SyntaxFacts.GetKeywordKind(pg) != CS.SyntaxKind.None ? "@" + pg : pg)
                : (VB.SyntaxFacts.GetKeywordKind(pg) != VB.SyntaxKind.None ? $"[{pg}]" : pg);
            switch (passed.Kind)
            {
                case PassedKind.Literal:
                    string written = c.Language.StringValue(passed.Node) ?? "";
                    c.Edit(passed.Node, c.Language.StringLiteral((written.StartsWith('@') ? "@" : "") + pg, passed.Node), "parameter named as the converted routine's");
                    break;
                case PassedKind.Member:
                    c.Edit(passed.Node, identifier, "parameter named as the converted routine's");
                    break;
                case PassedKind.Implicit:
                    c.Edit(passed.Node, cs ? $"{identifier} = {passed.Node}" : $".{identifier} = {passed.Node}", "parameter named as the converted routine's");
                    break;
            }
        }
    }

    /// <summary><c>ParameterDirection.Output</c> (or another member) in the block.</summary>
    private static IEnumerable<SyntaxNode> Directions(CodeContext c, SyntaxNode scope, string member) => c.Root switch
    {
        CSS.CompilationUnitSyntax => scope.DescendantNodes().OfType<CSS.MemberAccessExpressionSyntax>()
            .Where(m => m.Expression.ToString().EndsWith("ParameterDirection", StringComparison.Ordinal) && m.Name.Identifier.ValueText == member),
        _ => scope.DescendantNodes().OfType<VBS.MemberAccessExpressionSyntax>()
            .Where(m => m.Expression?.ToString().EndsWith("ParameterDirection", StringComparison.OrdinalIgnoreCase) == true
                        && m.Name.Identifier.ValueText.Equals(member, StringComparison.OrdinalIgnoreCase)),
    };

    private static bool IsStoredProcedure(string text) =>
        text.EndsWith("CommandType.StoredProcedure", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> CommandTypes = new(StringComparer.OrdinalIgnoreCase) { "SqlCommand", "NpgsqlCommand" };

    private static class CSharp
    {
        public static List<Site> Sites(CSS.CompilationUnitSyntax root, CodeContext c)
        {
            var sites = new List<Site>();
            // ADO.NET: a command whose CommandType is set to StoredProcedure, in an initializer or by assignment.
            foreach (var marker in root.DescendantNodes().OfType<CSS.AssignmentExpressionSyntax>().Where(a => IsStoredProcedure(a.Right.ToString())
                         && (a.Left is CSS.IdentifierNameSyntax { Identifier.ValueText: "CommandType" } || a.Left is CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandType" })))
            {
                var scope = (SyntaxNode?)marker.FirstAncestorOrSelf<CSS.BlockSyntax>() ?? root;
                string? variable = null;
                SyntaxNode? name = null;
                if (marker.Parent is CSS.InitializerExpressionSyntax { Parent: CSS.BaseObjectCreationExpressionSyntax creation })
                {
                    name = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression
                           ?? (creation.Initializer?.Expressions.OfType<CSS.AssignmentExpressionSyntax>()
                               .FirstOrDefault(a => a.Left is CSS.IdentifierNameSyntax { Identifier.ValueText: "CommandText" })?.Right);
                    variable = (creation.Parent?.Parent as CSS.VariableDeclaratorSyntax)?.Identifier.ValueText;
                }
                else if (marker.Left is CSS.MemberAccessExpressionSyntax { Expression: CSS.IdentifierNameSyntax v })
                {
                    variable = v.Identifier.ValueText;
                    name = CommandText(scope, variable);
                }
                if (name is null || Literal(name, root) is not { } literal)
                    continue;
                sites.Add(new Site(literal.Node, literal.Value, [marker.Right], scope, variable is null ? null : Parameters(scope, variable)));
            }
            // Dapper: commandType: CommandType.StoredProcedure.
            if (c.Imports.Contains("Dapper"))
            {
                foreach (var call in root.DescendantNodes().OfType<CSS.InvocationExpressionSyntax>())
                {
                    var marker = call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "commandType" && IsStoredProcedure(a.Expression.ToString()));
                    if (marker is null || call.ArgumentList.Arguments.Count == 0)
                        continue;
                    var sql = call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "sql") ?? call.ArgumentList.Arguments[0];
                    if (Literal(sql.Expression, root) is not { } literal)
                        continue;
                    var param = call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "param")
                                ?? (call.ArgumentList.Arguments.Count > 1 && call.ArgumentList.Arguments[1].NameColon is null ? call.ArgumentList.Arguments[1] : null);
                    IReadOnlyList<Passed>? names = param?.Expression is CSS.AnonymousObjectCreationExpressionSyntax anonymous
                        ? anonymous.Initializers.Select(i => i.NameEquals is { } named
                            ? new Passed(named.Name.Identifier.ValueText, named.Name, PassedKind.Member)
                            : new Passed(i.Expression.ToString().Split('.')[^1], i.Expression, PassedKind.Implicit)).ToList()
                        : param is null ? [] : null;
                    var scope = (SyntaxNode?)call.FirstAncestorOrSelf<CSS.BlockSyntax>() ?? root;
                    if (names is null && param?.Expression is CSS.IdentifierNameSyntax dynamicParameters)
                        names = DynamicParameters(scope, dynamicParameters.Identifier.ValueText);
                    sites.Add(new Site(literal.Node, literal.Value, [marker.Expression], scope, names));
                }
            }
            return sites;
        }

        private static SyntaxNode? CommandText(SyntaxNode scope, string variable)
        {
            var assigned = scope.DescendantNodes().OfType<CSS.AssignmentExpressionSyntax>()
                .FirstOrDefault(a => a.Left is CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText", Expression: CSS.IdentifierNameSyntax v } && v.Identifier.ValueText == variable);
            if (assigned is not null)
                return assigned.Right;
            var initial = scope.DescendantNodes().OfType<CSS.VariableDeclaratorSyntax>().FirstOrDefault(d => d.Identifier.ValueText == variable)?.Initializer?.Value;
            return initial is CSS.BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [var first, ..] } created
                   && (created is not CSS.ObjectCreationExpressionSyntax o || CommandTypes.Contains(o.Type.ToString().Split('.')[^1]))
                ? first.Expression
                : null;
        }

        /// <summary>Parameters added to <paramref name="variable"/>'s Parameters: <c>Add("@x", …)</c>, <c>AddWithValue("@x", …)</c>, <c>Add(new SqlParameter("@x", …))</c>.</summary>
        private static List<Passed> Parameters(SyntaxNode scope, string variable)
        {
            var names = new List<Passed>();
            foreach (var call in scope.DescendantNodes().OfType<CSS.InvocationExpressionSyntax>())
            {
                if (call.Expression is not CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" or "AddWithValue", Expression: CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Parameters", Expression: CSS.IdentifierNameSyntax v } }
                    || v.Identifier.ValueText != variable || call.ArgumentList.Arguments.Count == 0)
                    continue;
                var first = call.ArgumentList.Arguments[0].Expression;
                if (first is CSS.BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [var p, ..] })
                    first = p.Expression;
                if (first is CSS.LiteralExpressionSyntax literal && literal.IsKind(CS.SyntaxKind.StringLiteralExpression))
                    names.Add(new Passed(literal.Token.ValueText.TrimStart('@'), literal, PassedKind.Literal));
            }
            return names;
        }

        private static List<Passed> DynamicParameters(SyntaxNode scope, string variable) =>
            scope.DescendantNodes().OfType<CSS.InvocationExpressionSyntax>()
                .Where(call => call.Expression is CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add", Expression: CSS.IdentifierNameSyntax v } && v.Identifier.ValueText == variable)
                .Select(call => call.ArgumentList.Arguments.FirstOrDefault()?.Expression)
                .OfType<CSS.LiteralExpressionSyntax>().Where(l => l.IsKind(CS.SyntaxKind.StringLiteralExpression))
                .Select(l => new Passed(l.Token.ValueText.TrimStart('@'), l, PassedKind.Literal)).ToList();

        /// <summary>A string literal, or the literal a const field holds.</summary>
        private static (SyntaxNode Node, string Value)? Literal(SyntaxNode expression, SyntaxNode root)
        {
            if (expression is CSS.LiteralExpressionSyntax literal && literal.IsKind(CS.SyntaxKind.StringLiteralExpression))
                return (literal, literal.Token.ValueText);
            string? name = expression switch
            {
                CSS.IdentifierNameSyntax id => id.Identifier.ValueText,
                CSS.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                _ => null,
            };
            var declared = root.DescendantNodes().OfType<CSS.VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == name && v.Parent?.Parent is CSS.FieldDeclarationSyntax f && f.Modifiers.Any(CS.SyntaxKind.ConstKeyword));
            return declared?.Initializer?.Value is CSS.LiteralExpressionSyntax value && value.IsKind(CS.SyntaxKind.StringLiteralExpression)
                ? (value, value.Token.ValueText)
                : null;
        }
    }

    private static class VisualBasic
    {
        public static List<Site> Sites(VBS.CompilationUnitSyntax root, CodeContext c)
        {
            var sites = new List<Site>();
            var markers = root.DescendantNodes().Select(n => n switch
            {
                VBS.AssignmentStatementSyntax { Left: VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var p, Expression: VBS.IdentifierNameSyntax v } } a
                    when p.Equals("CommandType", StringComparison.OrdinalIgnoreCase) && IsStoredProcedure(a.Right.ToString()) => (Marker: (SyntaxNode)a.Right, Variable: (string?)v.Identifier.ValueText, Creation: (VBS.ObjectCreationExpressionSyntax?)null),
                VBS.NamedFieldInitializerSyntax f when f.Name.Identifier.ValueText.Equals("CommandType", StringComparison.OrdinalIgnoreCase) && IsStoredProcedure(f.Expression.ToString())
                    => (f.Expression, (f.Parent?.Parent?.Parent?.Parent as VBS.VariableDeclaratorSyntax)?.Names.FirstOrDefault()?.Identifier.ValueText
                        ?? (f.Parent?.Parent?.Parent?.Parent?.Parent as VBS.VariableDeclaratorSyntax)?.Names.FirstOrDefault()?.Identifier.ValueText,
                        f.FirstAncestorOrSelf<VBS.ObjectCreationExpressionSyntax>()),
                _ => (null!, null, null),
            }).Where(x => x.Marker is not null).ToList();
            foreach (var (marker, variable, creation) in markers)
            {
                var scope = (SyntaxNode?)marker.FirstAncestorOrSelf<VBS.MethodBlockBaseSyntax>() ?? root;
                SyntaxNode? name = creation?.ArgumentList?.Arguments.OfType<VBS.SimpleArgumentSyntax>().FirstOrDefault()?.Expression
                                   ?? (variable is null ? null : CommandText(scope, variable));
                if (name is null || Literal(name, root) is not { } literal)
                    continue;
                sites.Add(new Site(literal.Node, literal.Value, [marker], scope, variable is null ? null : Parameters(scope, variable)));
            }
            if (c.Imports.Contains("Dapper"))
            {
                foreach (var call in root.DescendantNodes().OfType<VBS.InvocationExpressionSyntax>())
                {
                    var args = call.ArgumentList?.Arguments.OfType<VBS.SimpleArgumentSyntax>().ToList() ?? [];
                    var marker = args.FirstOrDefault(a => a.NameColonEquals?.Name.Identifier.ValueText.Equals("commandType", StringComparison.OrdinalIgnoreCase) == true
                                                          && IsStoredProcedure(a.Expression.ToString()));
                    if (marker is null || args.Count == 0 || Literal(args[0].Expression, root) is not { } literal)
                        continue;
                    var param = args.Count > 1 && args[1].NameColonEquals is null ? args[1] : null;
                    IReadOnlyList<Passed>? names = param?.Expression is VBS.AnonymousObjectCreationExpressionSyntax anonymous
                        ? anonymous.Initializer.Initializers.Select(i => i is VBS.NamedFieldInitializerSyntax nf
                            ? new Passed(nf.Name.Identifier.ValueText, nf.Name, PassedKind.Member)
                            : new Passed(i.ToString().Split('.')[^1], i, PassedKind.Implicit)).ToList()
                        : param is null ? [] : null;
                    sites.Add(new Site(literal.Node, literal.Value, [marker.Expression], (SyntaxNode?)call.FirstAncestorOrSelf<VBS.MethodBlockBaseSyntax>() ?? root, names));
                }
            }
            return sites;
        }

        private static SyntaxNode? CommandText(SyntaxNode scope, string variable)
        {
            var assigned = scope.DescendantNodes().OfType<VBS.AssignmentStatementSyntax>()
                .FirstOrDefault(a => a.Left is VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var p, Expression: VBS.IdentifierNameSyntax v }
                                     && p.Equals("CommandText", StringComparison.OrdinalIgnoreCase) && v.Identifier.ValueText.Equals(variable, StringComparison.OrdinalIgnoreCase));
            if (assigned is not null)
                return assigned.Right;
            var clause = scope.DescendantNodes().OfType<VBS.VariableDeclaratorSyntax>()
                .FirstOrDefault(d => d.Names.Any(n => n.Identifier.ValueText.Equals(variable, StringComparison.OrdinalIgnoreCase)))?.AsClause;
            return clause is VBS.AsNewClauseSyntax { NewExpression: VBS.ObjectCreationExpressionSyntax created }
                   && CommandTypes.Contains(created.Type.ToString().Split('.')[^1])
                   && created.ArgumentList?.Arguments.OfType<VBS.SimpleArgumentSyntax>().FirstOrDefault() is { } first
                ? first.Expression
                : null;
        }

        private static List<Passed> Parameters(SyntaxNode scope, string variable)
        {
            var names = new List<Passed>();
            foreach (var call in scope.DescendantNodes().OfType<VBS.InvocationExpressionSyntax>())
            {
                if (call.Expression is not VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method, Expression: VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var parameters, Expression: VBS.IdentifierNameSyntax v } }
                    || !method.Equals("Add", StringComparison.OrdinalIgnoreCase) && !method.Equals("AddWithValue", StringComparison.OrdinalIgnoreCase)
                    || !parameters.Equals("Parameters", StringComparison.OrdinalIgnoreCase) || !v.Identifier.ValueText.Equals(variable, StringComparison.OrdinalIgnoreCase))
                    continue;
                var first = (call.ArgumentList?.Arguments.FirstOrDefault() as VBS.SimpleArgumentSyntax)?.Expression;
                if (first is VBS.ObjectCreationExpressionSyntax { ArgumentList.Arguments: [VBS.SimpleArgumentSyntax p, ..] })
                    first = p.Expression;
                if (first is VBS.LiteralExpressionSyntax literal && literal.IsKind(VB.SyntaxKind.StringLiteralExpression))
                    names.Add(new Passed(literal.Token.ValueText.TrimStart('@'), literal, PassedKind.Literal));
            }
            return names;
        }

        private static (SyntaxNode Node, string Value)? Literal(SyntaxNode expression, SyntaxNode root)
        {
            if (expression is VBS.LiteralExpressionSyntax literal && literal.IsKind(VB.SyntaxKind.StringLiteralExpression))
                return (literal, literal.Token.ValueText);
            string? name = expression switch
            {
                VBS.IdentifierNameSyntax id => id.Identifier.ValueText,
                VBS.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                _ => null,
            };
            var declared = root.DescendantNodes().OfType<VBS.VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Names.Any(n => n.Identifier.ValueText.Equals(name, StringComparison.OrdinalIgnoreCase))
                                     && v.Parent is VBS.FieldDeclarationSyntax f && f.Modifiers.Any(m => m.IsKind(VB.SyntaxKind.ConstKeyword)));
            return declared?.Initializer?.Value is VBS.LiteralExpressionSyntax value && value.IsKind(VB.SyntaxKind.StringLiteralExpression)
                ? (value, value.Token.ValueText)
                : null;
        }
    }
}
