using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <param name="Sql">The PostgreSQL script; untranslated statements are kept in it as comments.</param>
public sealed record ScriptTranslation(string Sql, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Translates an ad-hoc T-SQL script (application queries, reports, maintenance scripts) to
/// PostgreSQL, statement by statement, against a database's semantic model when one is given
/// (without it, column types are unknown and the conversions that depend on them are not made).
/// GO, USE and session SET options are dropped; DDL belongs to <c>sql2pg schema</c> and procedural
/// statements to Stage 3, so both stay as comments. mssql_compat helpers the script needs are
/// created at its top, so the output runs on its own.
/// </summary>
public static class ScriptTranslator
{
    public static ScriptTranslation Translate(string tsql, SemanticModel model, ConversionOptions options, string source = "script")
    {
        var diagnostics = new List<Diagnostic>();
        var owner = new ObjectName("", source);
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(tsql), out var errors);
        if (errors.Count > 0)
            return new ScriptTranslation($"/* sql2pg:untranslated {DiagnosticCodes.ParseError}\n{tsql.Replace("*/", "* /")}\n*/\n",
                errors.Select(e => new Diagnostic(DiagnosticCodes.ParseError, Severity.Error, owner, $"{source}({e.Line},{e.Column}): {e.Message}",
                    new SourceSpan(e.Line, e.Column, 0))).ToList());

        var names = new NameMapper(options);
        var expressions = new ExpressionTranslator(options, names, new TypeMapper(options, names), model);
        var queries = new QueryTranslator(expressions);
        var body = new StringBuilder();
        var compat = new SortedSet<string>(StringComparer.Ordinal);
        // One scope for the script, as for a session: a temp table one statement creates is known
        // to the next, across GO too.
        var scope = new Scope();
        foreach (var batch in ((TSqlScript)fragment).Batches)
        {
            foreach (var statement in batch.Statements)
            {
                var context = new ExpressionContext(scope, SemanticModel.DefaultSchema, owner);
                body.Append(Statement(statement, context, queries, names, model, options));
                diagnostics.AddRange(context.Diagnostics);
                compat.UnionWith(context.CompatFunctions);
            }
        }

        var sb = new StringBuilder();
        foreach (var helper in SchemaConverter.CompatObjects(compat, options))
            sb.Append(helper.Sql).Append('\n');
        sb.Append(body);
        return new ScriptTranslation(sb.ToString(), diagnostics);
    }

    private static string Statement(TSqlStatement statement, ExpressionContext c, QueryTranslator queries, NameMapper names,
        SemanticModel model, ConversionOptions options)
    {
        string original = ExpressionTranslator.Text(statement);
        switch (statement)
        {
            case UseStatement use:
                c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"USE {use.DatabaseName.Value} is dropped: a PostgreSQL connection stays in its database.");
                return "";
            case PredicateSetStatement or SetCommandStatement or SetRowCountStatement or SetTransactionIsolationLevelStatement:
                c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"'{original.Trim()}' sets a SQL Server session option and is dropped.");
                return "";
            case SetIdentityInsertStatement ins:
                return IdentityInsert(ins, c, names, model);
            case SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement:
                try
                {
                    return PgWriter.Write(queries.Statement(statement, c), options.TargetVersion) + "\n";
                }
                catch (UntranslatableException ex)
                {
                    c.Report(ex.Code, Severity.Error, ex.Message);
                    return PgWriter.Write(new PgUntranslated(ex.Code, original), options.TargetVersion) + "\n";
                }
            case CreateTableStatement or AlterTableStatement or CreateIndexStatement or CreateViewStatement or CreateTypeStatement
                or CreateSequenceStatement or CreateSchemaStatement or DropObjectsStatement:
                c.Report(DiagnosticCodes.DdlInScript, Severity.Error, "DDL in an ad-hoc script: convert schema objects with 'sql2pg schema', which orders them and creates their keys and indexes.");
                return PgWriter.Write(new PgUntranslated(DiagnosticCodes.DdlInScript, original), options.TargetVersion) + "\n";
            default:
                c.Report(DiagnosticCodes.ProceduralStatement, Severity.Error, $"'{statement.GetType().Name}' is procedural T-SQL, translated in Stage 3.");
                return PgWriter.Write(new PgUntranslated(DiagnosticCodes.ProceduralStatement, original), options.TargetVersion) + "\n";
        }
    }

    /// <summary>
    /// SET IDENTITY_INSERT is not needed (identities are GENERATED BY DEFAULT and take explicit
    /// values). When it is turned off, the identity's sequence is moved past the values inserted.
    /// </summary>
    private static string IdentityInsert(SetIdentityInsertStatement ins, ExpressionContext c, NameMapper names, SemanticModel model)
    {
        c.Report(DiagnosticCodes.IdentityInsert, Severity.Info, "SET IDENTITY_INSERT is dropped: identity columns accept explicit values.");
        if (ins.IsOn)
            return "";
        var table = model.ResolveRelation(ins.Table, c.Schema) is { } t ? model.Table(t) : null;
        var identity = table?.Columns.FirstOrDefault(col => col.Identity is not null);
        if (table is null || identity is null)
            return "";
        var target = names.Relation(table.Name);
        string column = NameMapper.Quote(names.Column(table.Name, identity.Name));
        return $"SELECT setval(pg_get_serial_sequence('{target.Qualified.Replace("'", "''")}', '{names.Column(table.Name, identity.Name).Replace("'", "''")}'), "
               + $"COALESCE((SELECT max({column}) FROM {target.Qualified}), 1));\n\n";
    }
}
