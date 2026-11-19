using System.Text.Json;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Data;

/// <summary>What the last sync of a table reached: its mode and its high-water mark.</summary>
/// <param name="Mode"><c>rowversion</c>, <c>changetracking</c> or <c>full</c>.</param>
/// <param name="RowVersion">The database's last rowversion (@@DBTS) before the last pass, as 0x…</param>
/// <param name="ChangeVersion">CHANGE_TRACKING_CURRENT_VERSION() before the last pass.</param>
public sealed record SyncTableState(string Mode, string? RowVersion = null, long? ChangeVersion = null);

/// <summary>
/// <c>sql2pg data --since state.json</c>: copies what changed since the last run, for a cut-over
/// with a short outage. The first run copies every table whole and records its high-water mark;
/// later runs bring each table up to date by the means it has:
/// <list type="bullet">
/// <item>a rowversion column and a primary key: the rows whose rowversion is above the mark;</item>
/// <item>change tracking and a primary key: CHANGETABLE(CHANGES …), deletes included;</item>
/// <item>neither: the whole table again.</item>
/// </list>
/// Changed rows are upserted (<c>INSERT … ON CONFLICT (pk) DO UPDATE</c>) from a staging table, and
/// tracked deletes are applied, in one transaction per table. Identities and sequences then move
/// past the new values, as after a full copy.
/// </summary>
public static class IncrementalCopier
{
    public const string RowVersionMode = "rowversion";
    public const string ChangeTrackingMode = "changetracking";
    public const string FullMode = "full";

    public static async Task<DataCopyResult> SyncAsync(SourceDatabase db, ConversionOptions conversion, string sqlServer, string postgres,
        string statePath, DataCopyOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DataCopyOptions();
        var translator = new SchemaTranslator(conversion);
        var translated = translator.Translate(db);
        var plans = TablePlan.Build(db, translated, translator.Names).Where(p => DataCopier.Selected(p.Source.Name, options.Tables)).ToList();
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<Dictionary<string, SyncTableState>>(File.ReadAllText(statePath)) ?? []
            : new Dictionary<string, SyncTableState>();

        var results = new List<TableCopyResult>();
        var errors = new Dictionary<ObjectName, string>();
        foreach (var plan in plans)
        {
            string key = plan.Source.Name.ToString();
            var diagnostics = new List<Diagnostic>();
            var states = plan.Columns.Select(_ => new ColumnState()).ToArray();
            long rows = 0;
            try
            {
                var (mode, rowVersion, changeVersion, why) = await ModeAsync(plan, sqlServer, ct);
                state.TryGetValue(key, out var prior);
                bool whole = prior is null || prior.Mode != mode || mode == FullMode;
                if (!whole && mode == ChangeTrackingMode && await MinValidVersionAsync(plan, sqlServer, ct) > prior!.ChangeVersion)
                {
                    whole = true;
                    why = "its change tracking no longer reaches back to the last sync (the retention period passed)";
                }
                if (whole)
                {
                    rows = await DataCopier.CopyTableAsync(plan, states, sqlServer, postgres, options, ct);
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.IncrementalSync, Severity.Info, plan.Source.Name,
                        prior is null
                            ? $"{key}: copied whole ({rows} rows), the first pass; later passes use {Describe(mode)}."
                            : $"{key}: copied whole again ({rows} rows), because {why}."));
                }
                else if (mode == RowVersionMode)
                {
                    var rv = plan.Columns.First(c => c.Source.Type.Name is "timestamp" or "rowversion").Source.Name;
                    rows = await UpsertAsync(plan, $"[{rv.Replace("]", "]]")}] > {prior!.RowVersion}", null, sqlServer, postgres, states, options, ct);
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.IncrementalSync, Severity.Info, plan.Source.Name,
                        $"{key}: {rows} rows changed since rowversion {prior.RowVersion} upserted. Deleted rows are not seen without change tracking: they stay in PostgreSQL."));
                }
                else
                {
                    string changes = $"CHANGETABLE(CHANGES {plan.SourceName}, {prior!.ChangeVersion}) AS ct";
                    string sameKey = string.Join(" AND ", Key(plan).Select(c => $"ct.[{c.Source.Name.Replace("]", "]]")}] = {plan.SourceName}.[{c.Source.Name.Replace("]", "]]")}]"));
                    rows = await UpsertAsync(plan,
                        $"EXISTS (SELECT 1 FROM {changes} WHERE ct.SYS_CHANGE_OPERATION <> 'D' AND {sameKey})",
                        $"SELECT {string.Join(", ", Key(plan).SelectMany(c => c.SourceSelect))} FROM {changes} WHERE ct.SYS_CHANGE_OPERATION = 'D'",
                        sqlServer, postgres, states, options, ct);
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.IncrementalSync, Severity.Info, plan.Source.Name,
                        $"{key}: {rows} rows changed or deleted since change tracking version {prior.ChangeVersion} applied."));
                }
                state[key] = new SyncTableState(mode, rowVersion, changeVersion);
                File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is SqlException or NpgsqlException or InvalidOperationException or InvalidCastException or OverflowException or FormatException)
            {
                errors[plan.Source.Name] = ex.Message;
                diagnostics.Add(new Diagnostic(DiagnosticCodes.TableCopyFailed, Severity.Error, plan.Source.Name, $"Syncing {key} failed: {ex.Message}"));
            }
            results.Add(new TableCopyResult(plan.Source.Name, plan.Target.Qualified, rows, diagnostics, errors.GetValueOrDefault(plan.Source.Name)));
        }

        var finish = await DataCopier.FinishAsync(db, plans, translator.Names, postgres, errors, ct);
        return new DataCopyResult(results.Select(r => r with { Diagnostics = [.. r.Diagnostics, .. finish.Where(d => d.Object == r.Table)] }).ToList());
    }

    private static string Describe(string mode) => mode switch
    {
        RowVersionMode => "its rowversion column",
        ChangeTrackingMode => "change tracking",
        _ => "a whole copy (it has neither a rowversion column with a primary key nor change tracking)",
    };

    private static IReadOnlyList<ColumnPlan> Key(TablePlan plan) =>
        plan.Source.PrimaryKey!.Columns.Select(k => plan.Columns.First(c => c.Source.Name.Equals(k.Name, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>The table's mode and the marks to record, read before the pass so nothing changed during it is missed.</summary>
    private static async Task<(string Mode, string? RowVersion, long? ChangeVersion, string Why)> ModeAsync(TablePlan plan, string sqlServer, CancellationToken ct)
    {
        await using var conn = new SqlConnection(sqlServer);
        await conn.OpenAsync(ct);
        bool hasKey = plan.Source.PrimaryKey is { } pk && pk.Columns.All(k => plan.Columns.Any(c => c.Source.Name.Equals(k.Name, StringComparison.OrdinalIgnoreCase)));
        if (hasKey && plan.Columns.Any(c => c.Source.Type.Name is "timestamp" or "rowversion"))
        {
            await using var cmd = new SqlCommand("SELECT CONVERT(varchar(20), CAST(@@DBTS AS binary(8)), 1)", conn);
            return (RowVersionMode, (string)(await cmd.ExecuteScalarAsync(ct))!, null, "");
        }
        if (hasKey)
        {
            await using var cmd = new SqlCommand(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(N'{plan.SourceName.Replace("'", "''")}')) THEN CHANGE_TRACKING_CURRENT_VERSION() END", conn);
            if (await cmd.ExecuteScalarAsync(ct) is long version)
                return (ChangeTrackingMode, null, version, "");
        }
        return (FullMode, null, null, "it has neither a rowversion column with a primary key nor change tracking");
    }

    private static async Task<long> MinValidVersionAsync(TablePlan plan, string sqlServer, CancellationToken ct)
    {
        await using var conn = new SqlConnection(sqlServer);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'{plan.SourceName.Replace("'", "''")}'))", conn);
        return await cmd.ExecuteScalarAsync(ct) is long v ? v : long.MaxValue;
    }

    /// <summary>
    /// The rows <paramref name="where"/> selects, upserted through a staging table, and the keys
    /// <paramref name="deletedKeys"/> selects deleted, in one transaction. Returns the rows applied.
    /// </summary>
    private static async Task<long> UpsertAsync(TablePlan plan, string where, string? deletedKeys, string sqlServer, string postgres,
        ColumnState[] states, DataCopyOptions options, CancellationToken ct)
    {
        await using var pg = new NpgsqlConnection(postgres);
        await pg.OpenAsync(ct);
        await using var tx = await pg.BeginTransactionAsync(ct);
        var key = Key(plan);
        string Columns(IEnumerable<ColumnPlan> cs) => string.Join(", ", cs.Select(c => NameMapper.Quote(c.TargetName)));
        async Task Run(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, pg, tx);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await Run($"CREATE TEMP TABLE sql2pg_sync_rows ON COMMIT DROP AS SELECT {Columns(plan.Columns)} FROM {plan.Target.Qualified} WITH NO DATA");
        long rows = await DataCopier.StreamAsync(plan.Columns, plan.SelectSql(where), $"COPY sql2pg_sync_rows ({Columns(plan.Columns)}) FROM STDIN (FORMAT BINARY)",
            sqlServer, pg, states, options.CommandTimeoutSeconds, _ => { }, ct);
        var others = plan.Columns.Where(c => !key.Contains(c)).ToList();
        string conflict = others.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", others.Select(c => $"{NameMapper.Quote(c.TargetName)} = EXCLUDED.{NameMapper.Quote(c.TargetName)}"));
        await Run($"INSERT INTO {plan.Target.Qualified} ({Columns(plan.Columns)}) OVERRIDING SYSTEM VALUE SELECT {Columns(plan.Columns)} FROM sql2pg_sync_rows ON CONFLICT ({Columns(key)}) {conflict}");

        if (deletedKeys is not null)
        {
            await Run($"CREATE TEMP TABLE sql2pg_sync_deleted ON COMMIT DROP AS SELECT {Columns(key)} FROM {plan.Target.Qualified} WITH NO DATA");
            var keyStates = key.Select(_ => new ColumnState()).ToArray();
            rows += await DataCopier.StreamAsync(key, deletedKeys, $"COPY sql2pg_sync_deleted ({Columns(key)}) FROM STDIN (FORMAT BINARY)",
                sqlServer, pg, keyStates, options.CommandTimeoutSeconds, _ => { }, ct);
            string match = string.Join(" AND ", key.Select(c => $"t.{NameMapper.Quote(c.TargetName)} = d.{NameMapper.Quote(c.TargetName)}"));
            await Run($"DELETE FROM {plan.Target.Qualified} AS t USING sql2pg_sync_deleted AS d WHERE {match}");
        }
        await tx.CommitAsync(ct);
        return rows;
    }
}
