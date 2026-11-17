using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <param name="Sql">The PostgreSQL statements, separated by <c>;</c> and a new line, without a final <c>;</c>.</param>
/// <param name="CompatFunctions">The mssql_compat helpers the SQL calls (they must exist in the database).</param>
public sealed record CommandTranslation(string Sql, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> CompatFunctions);

/// <summary>
/// SQL an application sends as a command (ADO.NET CommandText, Dapper, EF Core raw SQL). An
/// undeclared <c>@name</c> is a command parameter and stays <c>@name</c> for Npgsql to bind.
/// Procedural T-SQL (variables, control flow) has no place in a command (S2P8012). Two
/// application idioms change shape:
/// <list type="bullet">
/// <item><c>INSERT …; SELECT SCOPE_IDENTITY()</c> becomes <c>INSERT … RETURNING</c> the identity
/// column (with the schema), or reads <c>lastval()</c> (without it, S2P8011).</item>
/// <item>a top-level <c>COUNT</c>/<c>SUM</c> that SQL Server types as int is cast to integer:
/// PostgreSQL's is bigint, which <c>(int)ExecuteScalar()</c> cannot unbox.</item>
/// </list>
/// </summary>
public static class CommandTranslator
{
    public static CommandTranslation Translate(string tsql, SemanticModel model, ConversionOptions options, string source = "command")
    {
        var owner = new ObjectName("", source);
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(tsql), out var errors);
        if (errors.Count > 0)
            return new CommandTranslation(tsql, errors.Select(e => new Diagnostic(DiagnosticCodes.ParseError, Severity.Error, owner,
                $"The SQL does not parse as T-SQL ({e.Line},{e.Column}): {e.Message}", new SourceSpan(e.Line, e.Column, 0))).ToList(), []);

        var names = new NameMapper(options);
        var expressions = new ExpressionTranslator(options, names, new TypeMapper(options, names), model)
        {
            VariableWriter = name => new PgKeyword("@" + name.TrimStart('@')),
        };
        var queries = new QueryTranslator(expressions);
        var procedural = new ProceduralTranslator(queries);
        // Every @name the command uses is a parameter (its type is the value the application binds).
        var scope = new Scope();
        var parameters = new ParameterFinder();
        fragment.Accept(parameters);
        foreach (string name in parameters.Names)
            scope.DefineVariable(name, null);
        var diagnostics = new List<Diagnostic>();
        var compat = new SortedSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        for (int i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            var c = new ExpressionContext(scope, SemanticModel.DefaultSchema, owner);
            try
            {
                if (s is InsertStatement insert && i + 1 < statements.Count && IdentityRead(statements[i + 1]) is { } read)
                {
                    parts.Add(InsertReturning(insert, read, c, queries, expressions, names, model, options));
                    i++;
                }
                else if (s is ExecuteStatement e && ProceduralTranslator.IsPlainCall(e, parameters: true))
                    parts.Add(Write(procedural.ScriptCall(e, c), options));
                else if (ProceduralTranslator.IsProcedural(s) || s is DeclareTableVariableStatement)
                    throw new UntranslatableException(DiagnosticCodes.ProceduralSqlInCommand,
                        $"Procedural T-SQL in a command ({s.GetType().Name.Replace("Statement", "")}): a PostgreSQL command is one or more plain statements. Move it into a function or procedure, or into the application code.");
                else if (s is PredicateSetStatement or SetCommandStatement)
                    c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"'{ExpressionTranslator.Text(s).Trim()}' sets a SQL Server session option and is dropped.");
                else if (s is SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or TruncateTableStatement)
                    parts.Add(Write(IntegerAggregates(s, queries.Statement(s, c), c, expressions), options));
                else
                    throw new UntranslatableException(DiagnosticCodes.ProceduralSqlInCommand,
                        $"'{s.GetType().Name.Replace("Statement", "")}' in a command is not translated.");
            }
            catch (UntranslatableException ex)
            {
                c.Report(ex.Code, Severity.Error, ex.Message);
            }
            diagnostics.AddRange(c.Diagnostics);
            compat.UnionWith(c.CompatFunctions);
        }
        return new CommandTranslation(string.Join(";\n", parts.Where(p => p.Length > 0)), diagnostics, compat.ToList());
    }

    private sealed class ParameterFinder : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(VariableReference node) => Names.Add(node.Name);
    }

    private static string Write(PgNode node, ConversionOptions options) => node switch
    {
        PgPlStatement call => PgWriter.PlStatement(call, options.TargetVersion).TrimEnd().TrimEnd(';'),
        PgStatement statement => PgWriter.Write(statement, options.TargetVersion).TrimEnd().TrimEnd(';'),
        _ => throw new InvalidOperationException($"Cannot write {node.GetType().Name}."),
    };

    /// <summary>The value a <c>SELECT SCOPE_IDENTITY()</c> (or <c>@@IDENTITY</c>, cast or not) reads, with its alias.</summary>
    private static SelectScalarExpression? IdentityRead(TSqlStatement s) =>
        s is SelectStatement { Into: null, QueryExpression: QuerySpecification { FromClause: null, WhereClause: null, SelectElements: [SelectScalarExpression element] } }
        && ReadsIdentity(element.Expression)
            ? element
            : null;

    private static bool ReadsIdentity(ScalarExpression e) => e switch
    {
        ParenthesisExpression p => ReadsIdentity(p.Expression),
        CastCall cast => ReadsIdentity(cast.Parameter),
        ConvertCall convert => ReadsIdentity(convert.Parameter),
        FunctionCall { FunctionName.Value: var name, Parameters.Count: 0 } => name.Equals("SCOPE_IDENTITY", StringComparison.OrdinalIgnoreCase),
        GlobalVariableExpression g => g.Name.Equals("@@IDENTITY", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// <c>INSERT …; SELECT SCOPE_IDENTITY()</c>: with the target's identity column known, one
    /// statement, <c>INSERT … RETURNING</c> it (as numeric, the type SCOPE_IDENTITY returns,
    /// under whatever cast the code wrote); without it, the INSERT and <c>SELECT lastval()</c>.
    /// </summary>
    private static string InsertReturning(InsertStatement insert, SelectScalarExpression read, ExpressionContext c, QueryTranslator queries,
        ExpressionTranslator expressions, NameMapper names, SemanticModel model, ConversionOptions options)
    {
        var translated = queries.Statement(insert, c);
        string? column = insert.InsertSpecification.Target is NamedTableReference target
                         && model.ResolveRelation(target.SchemaObject, c.Schema) is { } name && model.Table(name) is { } table
                         && table.Columns.FirstOrDefault(col => col.Identity is not null) is { } identity
            ? names.Column(table.Name, identity.Name)
            : null;
        PgExpr identityValue = column is not null
            ? new PgCast(new PgColumnRef(column), "numeric")
            : new PgCast(new PgFunctionCall("lastval", []), "numeric");
        var saved = (expressions.ProceduralFunction, expressions.ProceduralGlobal);
        expressions.ProceduralFunction = (fn, _, _) => fn == "SCOPE_IDENTITY" ? identityValue : null;
        expressions.ProceduralGlobal = (g, _) => g == "@@IDENTITY" ? identityValue : null;
        try
        {
            var value = new PgTarget(expressions.Scalar(read.Expression, c), read.ColumnName is null ? null : names.Alias(read.ColumnName.Value));
            if (column is not null && translated is PgInsert pgInsert)
                return Write(pgInsert with { Returning = [value] }, options);
            c.Report(DiagnosticCodes.IdentityFromLastval, Severity.Warning,
                "SCOPE_IDENTITY() reads lastval(), the last value of any sequence in the session: give --source so the INSERT can return its identity column (RETURNING), or check that no trigger inserts elsewhere.");
            return Write(translated, options) + ";\n" + Write(new PgQueryStatement(new PgSelect { Targets = [value] }), options);
        }
        finally
        {
            (expressions.ProceduralFunction, expressions.ProceduralGlobal) = saved;
        }
    }

    /// <summary>Top-level COUNT/SUM results SQL Server types as int, cast to integer (PostgreSQL's are bigint).</summary>
    private static PgStatement IntegerAggregates(TSqlStatement s, PgStatement translated, ExpressionContext c, ExpressionTranslator expressions)
    {
        if (s is not SelectStatement { QueryExpression: QuerySpecification spec } || translated is not PgQueryStatement { Query: PgSelect select }
            || select.Targets.Count != spec.SelectElements.Count)
            return translated;
        var scope = expressions.Model.QueryScope(spec, c.Scope, c.Schema);
        var targets = select.Targets.Select((t, i) =>
            t.Expression is PgFunctionCall { Over: null, Name: var fn } aggregate && fn.ToLowerInvariant() is "count" or "sum"
            && spec.SelectElements[i] is SelectScalarExpression element && expressions.TypeOf(element.Expression, c.With(scope))?.Name == "int"
                ? t with { Expression = new PgCast(aggregate, "integer") }
                : t).ToList();
        return new PgQueryStatement(select with { Targets = targets });
    }
}
