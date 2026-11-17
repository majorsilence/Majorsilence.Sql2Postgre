using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <param name="Prelude">The mssql_compat helpers the statements need (empty when they need none).</param>
/// <param name="Body">The translated statements; untranslated ones are kept in it as comments.</param>
public sealed record ScriptTranslation(string Prelude, string Body, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The whole PostgreSQL script: the prelude, then the statements.</summary>
    public string Sql => Prelude + Body;
}

/// <summary>
/// Translates an ad-hoc T-SQL script (application queries, reports, maintenance scripts) to
/// PostgreSQL, statement by statement, against a database's semantic model when one is given
/// (without it, column types are unknown and the conversions that depend on them are not made).
/// GO, USE and session SET options are dropped; DDL belongs to <c>sql2pg schema</c>, so it stays
/// as a comment. A batch with variables or control flow becomes a <c>DO</c> block. mssql_compat helpers the script needs are
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
            return new ScriptTranslation("", $"/* sql2pg:untranslated {DiagnosticCodes.ParseError}\n{tsql.Replace("*/", "* /")}\n*/\n",
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
        var procedural = new ProceduralTranslator(queries);
        var transactionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ((TSqlScript)fragment).Batches)
        {
            if (batch.Statements.Any(ProceduralTranslator.IsProcedural))
            {
                // Variables and control flow: the batch runs as one DO block (its variables live as
                // long as the batch, as in T-SQL).
                var context = new ExpressionContext(scope.Child(), SemanticModel.DefaultSchema, owner);
                var block = procedural.Body(batch.Statements, BodySignature.AnonymousBlock, context);
                body.Append(PgWriter.Write(new PgDoBlock(block), options.TargetVersion)).Append('\n');
                diagnostics.AddRange(context.Diagnostics);
                compat.UnionWith(context.CompatFunctions);
                continue;
            }
            // Table variables are temporary tables that live as long as the batch.
            var tableVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            queries.TableVariable = name => tableVariables.GetValueOrDefault(name);
            foreach (var statement in batch.Statements)
            {
                var context = new ExpressionContext(scope, SemanticModel.DefaultSchema, owner);
                body.Append(Statement(statement, context, queries, procedural, names, model, options, transactionNames, tableVariables));
                diagnostics.AddRange(context.Diagnostics);
                compat.UnionWith(context.CompatFunctions);
            }
            queries.TableVariable = null;
            foreach (var table in tableVariables.Values)
                body.Append(PgWriter.Write(new PgDropTable(new PgName("", table)), options.TargetVersion)).Append('\n');
        }

        var prelude = new StringBuilder();
        foreach (var helper in SchemaConverter.CompatObjects(compat, options))
            prelude.Append(helper.Sql).Append('\n');
        return new ScriptTranslation(prelude.ToString(), body.ToString(), diagnostics);
    }

    private static string Statement(TSqlStatement statement, ExpressionContext c, QueryTranslator queries, ProceduralTranslator procedural,
        NameMapper names, SemanticModel model, ConversionOptions options, HashSet<string> transactionNames, Dictionary<string, string> tableVariables)
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
            case BeginTransactionStatement { Distributed: false } begin:
                if (begin.Name?.Value is { } transaction)
                    transactionNames.Add(transaction);
                return "BEGIN;\n\n";
            case CommitTransactionStatement:
                return "COMMIT;\n\n";
            case RollbackTransactionStatement rollback when rollback.Name?.Value is not { } rolledBack || transactionNames.Contains(rolledBack):
                return "ROLLBACK;\n\n";
            case RollbackTransactionStatement { Name.Value: { } savepoint }:
                return $"ROLLBACK TO SAVEPOINT {NameMapper.Quote(names.Alias(savepoint))};\n\n";
            case SaveTransactionStatement { Name.Value: { } savepoint }:
                return $"SAVEPOINT {NameMapper.Quote(names.Alias(savepoint))};\n\n";
            case ExecuteStatement execute:
                try
                {
                    return procedural.ScriptCall(execute, c) switch
                    {
                        PgPlStatement call => PgWriter.PlStatement(call, 0) + "\n",
                        var query => PgWriter.Write((PgStatement)query, options.TargetVersion) + "\n",
                    };
                }
                catch (UntranslatableException ex)
                {
                    c.Report(ex.Code, Severity.Error, ex.Message);
                    return PgWriter.Write(new PgUntranslated(ex.Code, original), options.TargetVersion) + "\n";
                }
            case var table when ProceduralTranslator.IsTableStatement(table):
                // Temp tables and table variables: temporary tables (other DDL is the schema's).
                return string.Concat(procedural.ScriptTable(table, c, tableVariables).Select(x => PgWriter.Write(x, options.TargetVersion))) + "\n";
            case SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or TruncateTableStatement:
                try
                {
                    return PgWriter.Write(queries.Statement(statement, c), options.TargetVersion) + "\n";
                }
                catch (UntranslatableException ex)
                {
                    c.Report(ex.Code, Severity.Error, ex.Message);
                    return PgWriter.Write(new PgUntranslated(ex.Code, original), options.TargetVersion) + "\n";
                }
            case var ddl when ProceduralTranslator.IsDdl(ddl):
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
