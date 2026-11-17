using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>
/// System-versioned temporal tables. PostgreSQL has none, so two row triggers do what SQL Server
/// does: <c>&lt;table&gt;_period</c> (BEFORE INSERT OR UPDATE) sets the period columns to the
/// transaction's start time in UTC and the end of time, and <c>&lt;table&gt;_history</c> (AFTER
/// UPDATE OR DELETE) copies the old row into the history table, its period ended at that time.
/// Switching SYSTEM_VERSIONING off disables the second; dropping the period disables the first.
/// The history table is an ordinary table, and the data copy keeps both tables' period values.
/// </summary>
public sealed partial class SchemaTranslator
{
    /// <summary>The trigger (and its function) that sets a temporal table's period columns.</summary>
    public static string PeriodTrigger(string table) => table + "_period";

    /// <summary>The trigger (and its function) that copies a temporal table's old rows into its history table.</summary>
    public static string HistoryTrigger(string table) => table + "_history";

    /// <summary>The transaction's start time in UTC, as SQL Server stamps period columns.</summary>
    public static PgExpr TransactionTimeUtc => new PgTemplate("(now() AT TIME ZONE 'UTC')", []);

    /// <summary>
    /// SQL Server's end of time (<c>9999-12-31 23:59:59.9999999</c>) at the column's precision,
    /// at most microseconds, so PostgreSQL does not round it into the year 10000.
    /// </summary>
    public static PgExpr EndOfTime(SqlServerType? type)
    {
        int scale = Math.Min(type?.Scale ?? 7, 6);
        string fraction = scale > 0 ? "." + new string('9', scale) : "";
        return new PgCast(PgLiteral.String($"9999-12-31 23:59:59{fraction}"), "timestamp");
    }

    private IEnumerable<TranslatedObject> Versioning(Table t, Semantics.SemanticModel model)
    {
        if (t.Period is not { } period)
            yield break;
        var table = _names.Relation(t.Name);
        string start = _names.Column(t.Name, period.StartColumn);
        string end = _names.Column(t.Name, period.EndColumn);
        var endType = t.Columns.FirstOrDefault(c => c.Name.Equals(period.EndColumn, StringComparison.OrdinalIgnoreCase))?.Type;
        var diagnostics = new List<Diagnostic>();
        var dependsOn = new List<ObjectName> { t.Name };

        var periodFunction = _names.Routine(new ObjectName(t.Name.Schema, PeriodTrigger(t.Name.Name)));
        var statements = new List<PgStatement>
        {
            new PgCreateRoutine(periodFunction, false, [], new PgPlBody(new PgBlock(
            [
                new PgAssign(new PgColumnRef(start, "new"), TransactionTimeUtc),
                new PgAssign(new PgColumnRef(end, "new"), EndOfTime(endType)),
                new PgReturn(new PgKeyword("NEW")),
            ]))) { Returns = "trigger" },
            new PgCreateTrigger(_names.Trigger(t.Name, PeriodTrigger(t.Name.Name)), table, "BEFORE", "INSERT OR UPDATE", periodFunction) { ForEachRow = true },
        };

        if (period.HistoryTable is { } historyName && model.Table(historyName) is { } history)
        {
            dependsOn.Add(historyName);
            // The history table's columns, each filled from the current table's column of the same name.
            var copied = history.Columns.Where(h => h.Computed is null)
                .Select(h => (Target: _names.Column(historyName, h.Name),
                    Source: t.Columns.FirstOrDefault(c => c.Name.Equals(h.Name, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Source is not null).ToList();
            var values = copied.Select(x => x.Source!.Name.Equals(period.EndColumn, StringComparison.OrdinalIgnoreCase)
                ? TransactionTimeUtc
                : (PgExpr)new PgColumnRef(_names.Column(t.Name, x.Source.Name), "old")).ToList();
            var historyFunction = _names.Routine(new ObjectName(t.Name.Schema, HistoryTrigger(t.Name.Name)));
            statements.Add(new PgCreateRoutine(historyFunction, false, [], new PgPlBody(new PgBlock(
            [
                new PgSqlStatement(new PgInsert(_names.Relation(historyName), copied.Select(x => x.Target).ToList(), new PgValues([values]))),
                new PgReturn(PgLiteral.Null),
            ]))) { Returns = "trigger" });
            statements.Add(new PgCreateTrigger(_names.Trigger(t.Name, HistoryTrigger(t.Name.Name)), table, "AFTER", "UPDATE OR DELETE", historyFunction) { ForEachRow = true });
            diagnostics.Add(new Diagnostic(DiagnosticCodes.SystemVersioning, Severity.Info, t.Name,
                $"{t.Name} is system-versioned: triggers set {period.StartColumn} and {period.EndColumn} and copy old rows into {historyName}. "
                + "The period columns are ordinary columns in PostgreSQL (a value written to them is overwritten), and FOR SYSTEM_TIME reads both tables."));
        }
        else
        {
            diagnostics.Add(new Diagnostic(DiagnosticCodes.SystemVersioning, Severity.Info, t.Name,
                $"{t.Name} has a PERIOD FOR SYSTEM_TIME{(period.HistoryTable is null ? " with SYSTEM_VERSIONING off" : $" whose history table {period.HistoryTable} is not in the source")}: a trigger sets {period.StartColumn} and {period.EndColumn}; no history is kept."));
        }
        yield return new TranslatedObject(new ObjectName(t.Name.Schema, PeriodTrigger(t.Name.Name)), ObjectKind.Trigger,
            $"{table.Qualified}.{NameMapper.Quote(_names.Trigger(t.Name, PeriodTrigger(t.Name.Name)))}", Phase.Triggers, statements, diagnostics, dependsOn);
    }
}
