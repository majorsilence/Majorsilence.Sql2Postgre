using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>The table, event and kind of trigger a body is translated for.</summary>
/// <param name="Table">The trigger's table or view, as PostgreSQL names it.</param>
/// <param name="Event"><c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c>.</param>
/// <param name="InsteadOf">An INSTEAD OF trigger (on a view, FOR EACH ROW); else AFTER, FOR EACH STATEMENT.</param>
/// <param name="Columns">The table's columns, which inserted and deleted have.</param>
/// <param name="Key">The PostgreSQL names of the table's primary key columns (empty for a view or a table without one).</param>
/// <param name="ColumnName">A source column's PostgreSQL name.</param>
public sealed record TriggerEvent(PgName Table, string Event, bool InsteadOf, IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> Key, Func<string, string> ColumnName)
{
    /// <summary>Whether the event has new rows (<c>inserted</c>).</summary>
    public bool HasInserted => Event is "INSERT" or "UPDATE";

    /// <summary>Whether the event has old rows (<c>deleted</c>).</summary>
    public bool HasDeleted => Event is "UPDATE" or "DELETE";
}

/// <summary>
/// Trigger bodies. An AFTER trigger is FOR EACH STATEMENT with transition tables named
/// <c>inserted</c> and <c>deleted</c>, so set-based T-SQL over them runs unchanged; the one the
/// event lacks (deleted on INSERT) is an empty query of the table. An INSTEAD OF trigger is FOR
/// EACH ROW, where inserted and deleted are the one row (<c>SELECT NEW.*</c>, <c>SELECT OLD.*</c>).
/// </summary>
public sealed partial class ProceduralTranslator
{
    private static readonly string[] PseudoTables = ["inserted", "deleted"];

    /// <summary>inserted / deleted as a FROM item for this event.</summary>
    private PgFromItem? PseudoTable(string written, string? alias, TriggerEvent trigger)
    {
        string? name = PseudoTables.FirstOrDefault(t => t.Equals(written, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;
        bool present = name == "inserted" ? trigger.HasInserted : trigger.HasDeleted;
        string asName = alias ?? name;
        if (!present)
            return new PgSubqueryRef(new PgSelect { Targets = [PgTarget.All()], From = [new PgTableRef(trigger.Table)], Where = new PgKeyword("false") }, asName, []);
        if (trigger.InsteadOf)
            return new PgSubqueryRef(new PgSelect { Targets = [PgTarget.All(name == "inserted" ? "new" : "old")] }, asName, []);
        return new PgTableRef(new PgName("", name), alias);
    }

    /// <summary>What a trigger function returns: NULL after the event, the row itself instead of it (so it counts as done).</summary>
    private static PgExpr TriggerResult(TriggerEvent trigger) =>
        !trigger.InsteadOf ? PgLiteral.Null : new PgKeyword(trigger.Event == "DELETE" ? "OLD" : "NEW");

    /// <summary>The start of a trigger body: inserted and deleted known to the scope, and @@ROWCOUNT the rows of the triggering statement.</summary>
    private IEnumerable<PgPlStatement> TriggerPrologue(TriggerEvent trigger, BodyState st)
    {
        var relation = new RelationInfo(null, ObjectKind.Table, trigger.Columns);
        foreach (string name in PseudoTables)
            st.C.Scope.DefineCte(name, relation);
        if (!st.ReadsRowCount)
            yield break;
        PgExpr rows = trigger.InsteadOf
            ? new PgLiteral("1")
            : new PgCast(new PgSubquery(new PgSelect
            {
                Targets = [new PgTarget(new PgFunctionCall("count", []) { Star = true })],
                From = [PseudoTable(trigger.HasInserted ? "inserted" : "deleted", null, trigger)!],
            }), "integer");
        yield return new PgAssign(Helper(RowCountVariable, "integer", st), rows);
    }

    /// <summary>
    /// UPDATE(column): true for INSERT, false for DELETE. For UPDATE, whether the value changed in
    /// some row (inserted and deleted joined on the primary key): PostgreSQL does not say which
    /// columns the statement set, and SQL Server's answer differs only for <c>SET c = c</c>.
    /// </summary>
    private PgExpr? TriggerPredicate(BooleanExpression e, ExpressionContext c, BodyState st)
    {
        if (e is not UpdateCall update || st.Signature.Trigger is not { } trigger)
            return null;
        switch (trigger.Event)
        {
            case "INSERT":
                return new PgKeyword("true");
            case "DELETE":
                return new PgKeyword("false");
            default:
                string column = NameMapper.Quote(trigger.ColumnName(update.Identifier.Value));
                if (trigger.InsteadOf)
                {
                    c.Report(DiagnosticCodes.UpdateColumnAssumed, Severity.Warning,
                        $"{ExpressionTranslator.Text(update).Trim()} is true when the row's value changed; SQL Server's is true whenever the statement set the column, even to the same value.");
                    return new PgKeyword($"NEW.{column} IS DISTINCT FROM OLD.{column}");
                }
                if (trigger.Key.Count == 0)
                {
                    c.Report(DiagnosticCodes.UpdateColumnAssumed, Severity.Warning,
                        $"{ExpressionTranslator.Text(update).Trim()} is true for every UPDATE: the table has no primary key to match inserted with deleted, and PostgreSQL does not tell a trigger which columns the statement set.");
                    return new PgKeyword("true");
                }
                c.Report(DiagnosticCodes.UpdateColumnAssumed, Severity.Warning,
                    $"{ExpressionTranslator.Text(update).Trim()} is true when the value changed in some row; SQL Server's is true whenever the statement set the column, even to the same value.");
                var keys = trigger.Key.Select(NameMapper.Quote).ToList();
                string join = string.Join(" AND ", keys.Select(k => $"sql2pg_d.{k} = sql2pg_i.{k}"));
                return new PgKeyword($"EXISTS (SELECT 1 FROM inserted AS sql2pg_i LEFT JOIN deleted AS sql2pg_d ON {join} "
                                     + $"WHERE sql2pg_d.{keys[0]} IS NULL OR sql2pg_i.{column} IS DISTINCT FROM sql2pg_d.{column})");
        }
    }

    /// <summary>ROLLBACK in a trigger ends the triggering statement and its transaction with an error, as SQL Server ends the batch.</summary>
    private static PgPlStatement TriggerRollback() =>
        new PgRaise("EXCEPTION", "transaction rolled back by trigger %") { Arguments = [new PgKeyword("TG_NAME")] };
}
