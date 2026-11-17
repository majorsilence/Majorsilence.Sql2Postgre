using Majorsilence.Sql2Postgre.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>
/// ADO.NET on SqlClient to Npgsql, in files that import <c>Microsoft.Data.SqlClient</c> or
/// <c>System.Data.SqlClient</c> (or name them in full):
/// <list type="bullet">
/// <item>namespaces and types;</item>
/// <item><c>SqlDbType.X</c> → <c>NpgsqlDbType.Y</c>;</item>
/// <item>error numbers compared on a caught SqlException → SQLSTATEs;</item>
/// <item>connection string literals;</item>
/// <item>reports for what has no Npgsql counterpart.</item>
/// </list>
/// </summary>
public sealed class AdoNetRule : ICodeRule
{
    public void Apply(CodeContext context)
    {
        ConnectionStringLiterals(context);
        switch (context.Root)
        {
            case CSS.CompilationUnitSyntax cs:
                new CSharp(context).Run(cs);
                break;
            case VBS.CompilationUnitSyntax vb:
                new VisualBasic(context).Run(vb);
                break;
        }
    }

    /// <summary>Any string literal that is a SQL Server connection string (a server key and a database or credential key).</summary>
    private static void ConnectionStringLiterals(CodeContext context)
    {
        foreach (var node in context.Root.DescendantNodes())
        {
            if (context.Language.StringValue(node) is not { } value || !ConnectionStrings.Looks(value))
                continue;
            var notes = new List<ConnectionStringNote>();
            if (ConnectionStrings.Convert(value, notes) is not { } converted)
                continue;
            context.Edit(node, context.Language.StringLiteral(converted, node), "connection string");
            foreach (var n in notes)
                context.Report(n.Code, n.Severity, node, n.Message);
        }
    }

    private static string NewLine(CodeContext c) => c.Text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>The state the two language walkers share: which tokens are already rewritten, and what to report once.</summary>
    private abstract class Walker(CodeContext context)
    {
        protected CodeContext C { get; } = context;
        protected readonly List<TextSpan> Consumed = [];
        protected bool Imported { get; } = context.Imports.Any(AdoNetMap.IsSqlClient);
        protected bool UsesDbType;
        private bool _exceptionReported;

        protected bool IsConsumed(TextSpan span) => Consumed.Any(c => c.Contains(span));

        protected void Consume(SyntaxNode node, string replacement, string reason)
        {
            C.Edit(node, replacement, reason);
            Consumed.Add(node.Span);
        }

        protected void ReportException(SyntaxNodeOrToken at)
        {
            if (_exceptionReported)
                return;
            _exceptionReported = true;
            C.Report(DiagnosticCodes.SqlExceptionToNpgsql, Severity.Warning, at,
                "SqlException becomes NpgsqlException: errors are identified by SqlState (a SQLSTATE such as 23505), not Number, and PostgreSQL's messages differ.");
        }

        /// <summary>A type name used in code: renamed, or reported when it has no counterpart.</summary>
        protected void TypeName(SyntaxToken identifier)
        {
            string name = identifier.ValueText;
            if (AdoNetMap.Types.TryGetValue(name, out var npgsql))
            {
                C.Edit(identifier, npgsql, $"{name} → {npgsql}");
                if (name == "SqlException")
                    ReportException(identifier);
            }
            else if (name == "SqlDbType")
            {
                C.Edit(identifier, "NpgsqlDbType", "SqlDbType → NpgsqlDbType");
                UsesDbType = true;
            }
            else if (AdoNetMap.Unsupported.TryGetValue(name, out var why))
                C.Report(why.Code, Severity.Error, identifier, why.Why);
            else if (AdoNetMap.SqlTypes.Contains(name) && C.Imports.Contains("System.Data.SqlTypes"))
                C.Report(DiagnosticCodes.SqlTypes, Severity.Error, identifier,
                    $"{name} (System.Data.SqlTypes) is SqlClient's: use the plain .NET type and DBNull checks.");
        }

        /// <summary>A member name (<c>x.Name</c>): SqlDbType property, SqlTypes readers, members with no counterpart.</summary>
        protected void MemberName(SyntaxToken identifier)
        {
            string name = identifier.ValueText;
            if (name == "SqlDbType")
            {
                C.Edit(identifier, "NpgsqlDbType", "SqlParameter.SqlDbType → NpgsqlParameter.NpgsqlDbType");
                UsesDbType = true;
            }
            else if (name.StartsWith("GetSql", StringComparison.Ordinal) && name.Length > 6 && char.IsUpper(name[6]))
                C.Report(DiagnosticCodes.SqlTypes, Severity.Error, identifier,
                    $"{name} returns a System.Data.SqlTypes value, which is SqlClient's: use the plain getter ({name.Replace("GetSql", "Get")}) and IsDBNull.");
            else if (AdoNetMap.UnsupportedMembers.TryGetValue(name, out var why))
                C.Report(DiagnosticCodes.SqlClientOnly, Severity.Error, identifier, why);
        }

        /// <summary><c>SqlDbType.X</c>: the whole member access becomes <c>NpgsqlDbType.Y</c>.</summary>
        protected void DbTypeMember(SyntaxNode access, string member)
        {
            UsesDbType = true;
            if (AdoNetMap.DbTypes.TryGetValue(member, out var npgsql) && npgsql is not null)
                Consume(access, $"NpgsqlDbType.{npgsql}", $"SqlDbType.{member} → NpgsqlDbType.{npgsql}");
            else if (member == "Structured")
            {
                C.Report(DiagnosticCodes.TableValuedParameter, Severity.Error, access,
                    "SqlDbType.Structured (a table-valued parameter) has no Npgsql counterpart: pass arrays (one per column, unnest in the SQL) or a composite-type array.");
                Consumed.Add(access.Span);
            }
            else
            {
                C.Report(DiagnosticCodes.SqlClientOnly, Severity.Error, access, $"SqlDbType.{member} has no NpgsqlDbType counterpart.");
                Consumed.Add(access.Span);
            }
        }

        /// <summary>The SQLSTATE test for <c>ex.Number == n</c> (or <c>!=</c>), or null for a number with no PostgreSQL counterpart.</summary>
        protected static string? StateTest(string variable, int number, bool equals, string eq, string ne, string or, string and)
        {
            if (!AdoNetMap.ErrorNumbers.TryGetValue(number, out var states))
                return null;
            var tests = states.Select(s => $"{variable}.SqlState {(equals ? eq : ne)} \"{s}\"").ToList();
            return tests.Count == 1 ? tests[0] : "(" + string.Join(equals ? $" {or} " : $" {and} ", tests) + ")";
        }

        protected void ExceptionMember(SyntaxNode at, string member)
        {
            if (AdoNetMap.ExceptionMembers.TryGetValue(member, out var why))
                C.Report(DiagnosticCodes.SqlExceptionMember, Severity.Error, at, why + ".");
        }

        protected void NpgsqlTypesImport(SyntaxNode? afterDirective, string directive, bool alreadyImported)
        {
            if (!UsesDbType || alreadyImported || afterDirective is null)
                return;
            C.Edit(new TextSpan(afterDirective.Span.End, 0), NewLine(C) + directive, "import NpgsqlTypes for NpgsqlDbType");
        }
    }

    private sealed class CSharp(CodeContext context) : Walker(context)
    {
        public void Run(CSS.CompilationUnitSyntax root)
        {
            CSS.UsingDirectiveSyntax? npgsqlUsing = null;
            bool hasNpgsql = C.Imports.Contains("Npgsql");
            foreach (var u in root.DescendantNodes().OfType<CSS.UsingDirectiveSyntax>())
            {
                if (u.Alias is not null || !u.StaticKeyword.IsKind(CS.SyntaxKind.None) || !AdoNetMap.IsSqlClient(u.Name?.ToString()))
                    continue;
                if (npgsqlUsing is null && !hasNpgsql)
                {
                    Consume(u.Name!, "Npgsql", "SqlClient namespace → Npgsql");
                    npgsqlUsing = u;
                }
                else
                    Consume(u, "", "duplicate import of Npgsql removed");
            }

            // Fully qualified names: Microsoft.Data.SqlClient.SqlConnection → Npgsql.NpgsqlConnection.
            foreach (var node in root.DescendantNodes().Where(n => n is CSS.QualifiedNameSyntax or CSS.MemberAccessExpressionSyntax))
            {
                if (IsConsumed(node.Span) || node.Parent is CSS.UsingDirectiveSyntax)
                    continue;
                string text = node.ToString();
                foreach (string ns in AdoNetMap.SqlClientNamespaces)
                {
                    if (!text.StartsWith(ns + ".", StringComparison.Ordinal))
                        continue;
                    string rest = text[(ns.Length + 1)..];
                    string type = rest.Split('.')[0];
                    if (AdoNetMap.Types.TryGetValue(type, out var npgsql) && !rest[type.Length..].Contains('.'))
                    {
                        Consume(node, "Npgsql." + npgsql + rest[type.Length..], $"{ns}.{type} → Npgsql.{npgsql}");
                        if (type == "SqlException")
                            ReportException(node);
                    }
                }
            }

            var exceptionVariables = ExceptionVariables(root);
            foreach (var binary in root.DescendantNodes().OfType<CSS.BinaryExpressionSyntax>())
            {
                if (!binary.IsKind(CS.SyntaxKind.EqualsExpression) && !binary.IsKind(CS.SyntaxKind.NotEqualsExpression))
                    continue;
                var (access, literal) = (binary.Left, binary.Right) switch
                {
                    (CSS.MemberAccessExpressionSyntax m, CSS.LiteralExpressionSyntax l) => (m, l),
                    (CSS.LiteralExpressionSyntax l, CSS.MemberAccessExpressionSyntax m) => (m, l),
                    _ => (null, null),
                };
                if (access is { Name.Identifier.ValueText: "Number", Expression: CSS.IdentifierNameSyntax variable }
                    && exceptionVariables.Contains(variable.Identifier.ValueText)
                    && literal!.Token.Value is int number)
                {
                    if (StateTest(variable.Identifier.ValueText, number, binary.IsKind(CS.SyntaxKind.EqualsExpression), "==", "!=", "||", "&&") is { } test)
                        Consume(binary, binary.Parent is CSS.IfStatementSyntax or CSS.ParenthesizedExpressionSyntax or CSS.WhenClauseSyntax && test.StartsWith('(')
                            ? test[1..^1] : test, $"SQL Server error {number} → SQLSTATE");
                    else
                    {
                        C.Report(DiagnosticCodes.SqlExceptionMember, Severity.Error, binary,
                            $"SQL Server error {number} has no PostgreSQL SQLSTATE this converter knows: find the SQLSTATE of the same error (PostgreSQL manual, appendix A) and test SqlState.");
                        Consumed.Add(binary.Span);
                    }
                }
            }

            foreach (var id in root.DescendantNodes().OfType<CSS.IdentifierNameSyntax>())
            {
                if (IsConsumed(id.Span) || id.Ancestors().Any(a => a is CSS.UsingDirectiveSyntax))
                    continue;
                bool member = id.Parent is CSS.MemberAccessExpressionSyntax ma && ma.Name == id
                              || id.Parent is CSS.MemberBindingExpressionSyntax
                              || id.Parent is CSS.QualifiedNameSyntax q && q.Right == id;
                bool initializerTarget = id.Parent is CSS.AssignmentExpressionSyntax a && a.Left == id && a.Parent is CSS.InitializerExpressionSyntax;
                if (member && id.Parent is CSS.MemberAccessExpressionSyntax { Expression: CSS.IdentifierNameSyntax receiver }
                    && exceptionVariables.Contains(receiver.Identifier.ValueText))
                {
                    ExceptionMember(id.Parent, id.Identifier.ValueText);
                    continue;
                }
                if (!Imported)
                    continue;
                if (member || initializerTarget)
                    MemberName(id.Identifier);
                else if (id.Identifier.ValueText == "SqlDbType" && id.Parent is CSS.MemberAccessExpressionSyntax dbType && dbType.Expression == id)
                    DbTypeMember(dbType, dbType.Name.Identifier.ValueText);
                else
                    TypeName(id.Identifier);
            }
            NpgsqlTypesImport(npgsqlUsing, "using NpgsqlTypes;", C.Imports.Contains("NpgsqlTypes"));
        }

        /// <summary>Names of variables holding a SqlException: catch declarations, parameters, locals and <c>is SqlException e</c> patterns.</summary>
        private static HashSet<string> ExceptionVariables(SyntaxNode root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.DescendantNodes().OfType<CSS.ParameterSyntax>())
                if (p.Type is { } type && IsSqlException(type))
                    names.Add(p.Identifier.ValueText);
            foreach (var d in root.DescendantNodes().OfType<CSS.VariableDeclarationSyntax>())
                if (IsSqlException(d.Type))
                    foreach (var v in d.Variables)
                        names.Add(v.Identifier.ValueText);
            foreach (var c in root.DescendantNodes().OfType<CSS.CatchDeclarationSyntax>())
                if (IsSqlException(c.Type) && !c.Identifier.IsKind(CS.SyntaxKind.None))
                    names.Add(c.Identifier.ValueText);
            foreach (var p in root.DescendantNodes().OfType<CSS.DeclarationPatternSyntax>())
                if (IsSqlException(p.Type) && p.Designation is CSS.SingleVariableDesignationSyntax v)
                    names.Add(v.Identifier.ValueText);
            return names;
        }

        private static bool IsSqlException(CSS.TypeSyntax type) =>
            type.ToString() is "SqlException" or "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException";
    }

    private sealed class VisualBasic(CodeContext context) : Walker(context)
    {
        public void Run(VBS.CompilationUnitSyntax root)
        {
            VBS.ImportsStatementSyntax? npgsqlImport = null;
            bool hasNpgsql = C.Imports.Contains("Npgsql");
            foreach (var statement in root.Imports)
            {
                foreach (var clause in statement.ImportsClauses.OfType<VBS.SimpleImportsClauseSyntax>())
                {
                    if (clause.Alias is not null || !AdoNetMap.IsSqlClient(clause.Name.ToString()))
                        continue;
                    if (npgsqlImport is null && !hasNpgsql)
                    {
                        Consume(clause.Name, "Npgsql", "SqlClient namespace → Npgsql");
                        npgsqlImport = statement;
                    }
                    else if (statement.ImportsClauses.Count == 1)
                        Consume(statement, "", "duplicate import of Npgsql removed");
                }
            }

            foreach (var node in root.DescendantNodes().Where(n => n is VBS.QualifiedNameSyntax or VBS.MemberAccessExpressionSyntax))
            {
                if (IsConsumed(node.Span) || node.Ancestors().Any(a => a is VBS.ImportsStatementSyntax))
                    continue;
                string text = node.ToString();
                foreach (string ns in AdoNetMap.SqlClientNamespaces)
                {
                    if (!text.StartsWith(ns + ".", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string rest = text[(ns.Length + 1)..];
                    string type = rest.Split('.')[0];
                    if (AdoNetMap.Types.TryGetValue(type, out var npgsql) && !rest[type.Length..].Contains('.'))
                    {
                        Consume(node, "Npgsql." + npgsql + rest[type.Length..], $"{ns}.{type} → Npgsql.{npgsql}");
                        if (type == "SqlException")
                            ReportException(node);
                    }
                }
            }

            var exceptionVariables = ExceptionVariables(root);
            foreach (var binary in root.DescendantNodes().OfType<VBS.BinaryExpressionSyntax>())
            {
                bool equals = binary.IsKind(VB.SyntaxKind.EqualsExpression);
                if (!equals && !binary.IsKind(VB.SyntaxKind.NotEqualsExpression))
                    continue;
                var (access, literal) = (binary.Left, binary.Right) switch
                {
                    (VBS.MemberAccessExpressionSyntax m, VBS.LiteralExpressionSyntax l) => (m, l),
                    (VBS.LiteralExpressionSyntax l, VBS.MemberAccessExpressionSyntax m) => (m, l),
                    _ => (null, null),
                };
                if (access is { Name.Identifier.ValueText: var member, Expression: VBS.IdentifierNameSyntax variable }
                    && member.Equals("Number", StringComparison.OrdinalIgnoreCase)
                    && exceptionVariables.Contains(variable.Identifier.ValueText)
                    && literal!.Token.Value is int number)
                {
                    if (StateTest(variable.Identifier.ValueText, number, equals, "=", "<>", "OrElse", "AndAlso") is { } test)
                        Consume(binary, test, $"SQL Server error {number} → SQLSTATE");
                    else
                    {
                        C.Report(DiagnosticCodes.SqlExceptionMember, Severity.Error, binary,
                            $"SQL Server error {number} has no PostgreSQL SQLSTATE this converter knows: find the SQLSTATE of the same error (PostgreSQL manual, appendix A) and test SqlState.");
                        Consumed.Add(binary.Span);
                    }
                }
            }

            foreach (var id in root.DescendantNodes().OfType<VBS.IdentifierNameSyntax>())
            {
                if (IsConsumed(id.Span) || id.Ancestors().Any(a => a is VBS.ImportsStatementSyntax))
                    continue;
                bool member = id.Parent is VBS.MemberAccessExpressionSyntax ma && ma.Name == id
                              || id.Parent is VBS.QualifiedNameSyntax q && q.Right == id;
                bool initializerTarget = id.Parent is VBS.NamedFieldInitializerSyntax f && f.Name == id;
                if (member && id.Parent is VBS.MemberAccessExpressionSyntax { Expression: VBS.IdentifierNameSyntax receiver }
                    && exceptionVariables.Contains(receiver.Identifier.ValueText))
                {
                    ExceptionMember(id.Parent, Pascal(id.Identifier.ValueText));
                    continue;
                }
                if (!Imported)
                    continue;
                if (member || initializerTarget)
                    MemberName(id.Identifier);
                else if (id.Identifier.ValueText.Equals("SqlDbType", StringComparison.OrdinalIgnoreCase)
                         && id.Parent is VBS.MemberAccessExpressionSyntax dbType && dbType.Expression == id)
                    DbTypeMember(dbType, Pascal(dbType.Name.Identifier.ValueText));
                else
                    TypeName(id.Identifier);
            }
            NpgsqlTypesImport(npgsqlImport, "Imports NpgsqlTypes", C.Imports.Contains("NpgsqlTypes"));
        }

        /// <summary>VB names are case-insensitive: the member as the map spells it.</summary>
        private static string Pascal(string name) =>
            AdoNetMap.DbTypes.Keys.Concat(AdoNetMap.ExceptionMembers.Keys).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;

        private static HashSet<string> ExceptionVariables(SyntaxNode root)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in root.DescendantNodes().OfType<VBS.CatchStatementSyntax>())
                if (c.IdentifierName is { } name && IsSqlException(c.AsClause?.Type))
                    names.Add(name.Identifier.ValueText);
            foreach (var p in root.DescendantNodes().OfType<VBS.ParameterSyntax>())
                if (IsSqlException(p.AsClause?.Type))
                    names.Add(p.Identifier.Identifier.ValueText);
            foreach (var d in root.DescendantNodes().OfType<VBS.VariableDeclaratorSyntax>())
                if (d.AsClause is VBS.SimpleAsClauseSyntax a && IsSqlException(a.Type))
                    foreach (var n in d.Names)
                        names.Add(n.Identifier.ValueText);
            return names;
        }

        private static bool IsSqlException(VBS.TypeSyntax? type) =>
            type?.ToString() is { } t && (t.Equals("SqlException", StringComparison.OrdinalIgnoreCase)
                                          || t.Equals("Microsoft.Data.SqlClient.SqlException", StringComparison.OrdinalIgnoreCase)
                                          || t.Equals("System.Data.SqlClient.SqlException", StringComparison.OrdinalIgnoreCase));
    }
}
