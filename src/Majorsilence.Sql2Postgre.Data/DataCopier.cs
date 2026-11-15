using System.Collections.Concurrent;
using System.Text.Json;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Data;

public sealed record DataCopyOptions
{
    /// <summary>How many tables (or ranges of one big table) are copied at once.</summary>
    public int MaxParallelTables { get; init; } = 4;

    /// <summary>A table with a single-column integer primary key and more rows than this is split into key ranges copied in parallel.</summary>
    public long SplitThresholdRows { get; init; } = 5_000_000;

    /// <summary>Where finished tables and ranges are recorded, so a rerun skips them; null for no resume.</summary>
    public string? StatePath { get; init; }

    /// <summary>Only these tables (<c>schema.table</c> or <c>table</c>, case-insensitive); empty for all.</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];

    /// <summary>Seconds a source query may run; 0 for no limit.</summary>
    public int CommandTimeoutSeconds { get; init; }
}

public sealed record TableProgress(ObjectName Table, long RowsCopied, bool Done);

/// <param name="Error">Why the table failed to copy, or null.</param>
public sealed record TableCopyResult(ObjectName Table, string Target, long Rows, IReadOnlyList<Diagnostic> Diagnostics, string? Error);

public sealed record DataCopyResult(IReadOnlyList<TableCopyResult> Tables)
{
    public IEnumerable<Diagnostic> Diagnostics => Tables.SelectMany(t => t.Diagnostics);
    public bool Succeeded => Tables.All(t => t.Error is null);
}

/// <summary>
/// Copies every converted table from SQL Server to PostgreSQL with binary <c>COPY</c>, several at
/// once, after the schema's tables exist (and, for speed, before its constraints and indexes).
/// Each table or key range not recorded as finished is cleared first, so a rerun never doubles
/// rows. Afterwards every identity's sequence is moved past the copied values, every sequence is
/// set to where SQL Server's was, and the tables are analyzed.
/// </summary>
public static class DataCopier
{
    private const int ProgressEvery = 100_000;

    public static async Task<DataCopyResult> CopyAsync(SourceDatabase db, ConversionOptions conversion,
        string sqlServerConnection, string postgresConnection, DataCopyOptions? options = null,
        IProgress<TableProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new DataCopyOptions();
        var translator = new SchemaTranslator(conversion);
        var translated = translator.Translate(db);
        var plans = TablePlan.Build(db, translated, translator.Names)
            .Where(p => Selected(p.Source.Name, options.Tables)).ToList();

        var state = CopyState.Load(options.StatePath);
        var units = new List<CopyUnit>();
        foreach (var plan in plans)
            units.AddRange(await UnitsAsync(plan, sqlServerConnection, options, ct));

        var rows = new ConcurrentDictionary<ObjectName, long>();
        var errors = new ConcurrentDictionary<ObjectName, string>();
        var columnStates = plans.ToDictionary(p => p.Source.Name, p => p.Columns.Select(_ => new ColumnState()).ToArray());

        await Parallel.ForEachAsync(units, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelTables), CancellationToken = ct },
            async (unit, token) =>
            {
                if (state.IsDone(unit.Key))
                    return;
                try
                {
                    long copied = await CopyUnitAsync(unit, columnStates[unit.Plan.Source.Name], sqlServerConnection, postgresConnection,
                        options, n => progress?.Report(new TableProgress(unit.Plan.Source.Name, n, false)), token);
                    rows.AddOrUpdate(unit.Plan.Source.Name, copied, (_, n) => n + copied);
                    state.MarkDone(unit.Key);
                    progress?.Report(new TableProgress(unit.Plan.Source.Name, copied, true));
                }
                catch (Exception ex) when (ex is SqlException or NpgsqlException or InvalidOperationException or InvalidCastException or OverflowException or FormatException)
                {
                    errors.AddOrUpdate(unit.Plan.Source.Name, ex.Message, (_, e) => e + "; " + ex.Message);
                }
            });

        var postCopy = await FinishAsync(db, plans, translator.Names, postgresConnection, errors, ct);

        var results = plans.Select(p =>
        {
            var diagnostics = new List<Diagnostic>();
            var states = columnStates[p.Source.Name];
            for (int i = 0; i < p.Columns.Count; i++)
            {
                if (states[i].NulsRemoved > 0)
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.NulCharacterRemoved, Severity.Warning, p.Source.Name,
                        $"{states[i].NulsRemoved} NUL character(s) were removed from {p.Source.Name}.{p.Columns[i].Source.Name}; PostgreSQL text cannot hold them."));
                if (states[i].ZmDropped > 0)
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.SpatialZmDropped, Severity.Warning, p.Source.Name,
                        $"{states[i].ZmDropped} value(s) of {p.Source.Name}.{p.Columns[i].Source.Name} had Z or M coordinates, which were dropped."));
            }
            errors.TryGetValue(p.Source.Name, out string? error);
            if (error is not null)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.TableCopyFailed, Severity.Error, p.Source.Name, $"Copying {p.Source.Name} failed: {error}"));
            diagnostics.AddRange(postCopy.Where(d => d.Object == p.Source.Name));
            return new TableCopyResult(p.Source.Name, p.Target.Qualified, rows.GetValueOrDefault(p.Source.Name), diagnostics, error);
        }).ToList();
        return new DataCopyResult(results);
    }

    private static bool Selected(ObjectName table, IReadOnlyList<string> filter) =>
        filter.Count == 0 || filter.Any(f => string.Equals(f, table.ToString(), StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(f, table.Name, StringComparison.OrdinalIgnoreCase));

    /// <param name="Low">Inclusive lower key bound, for a range of a split table.</param>
    /// <param name="High">Exclusive upper key bound, or inclusive for the last range.</param>
    private sealed record CopyUnit(TablePlan Plan, string Key, long? Low = null, long? High = null, bool LastRange = false);

    /// <summary>One unit per table, or per key range for a big table with an integer primary key.</summary>
    private static async Task<IReadOnlyList<CopyUnit>> UnitsAsync(TablePlan plan, string sqlServer, DataCopyOptions options, CancellationToken ct)
    {
        string key = plan.Source.Name.ToString();
        if (plan.SplitColumn is not { } split || (plan.Source.ApproximateRowCount ?? 0) <= options.SplitThresholdRows || options.MaxParallelTables <= 1)
            return [new CopyUnit(plan, key)];

        await using var conn = new SqlConnection(sqlServer);
        await conn.OpenAsync(ct);
        string col = $"[{split.Name.Replace("]", "]]")}]";
        await using var cmd = new SqlCommand($"SELECT CAST(MIN({col}) AS bigint), CAST(MAX({col}) AS bigint) FROM {plan.SourceName}", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.IsDBNull(0))
            return [new CopyUnit(plan, key)];
        long min = reader.GetInt64(0), max = reader.GetInt64(1);
        int parts = options.MaxParallelTables;
        var units = new List<CopyUnit>();
        decimal width = ((decimal)max - min + 1) / parts;
        for (int k = 0; k < parts; k++)
        {
            long low = min + (long)Math.Floor(width * k);
            long high = k == parts - 1 ? max : min + (long)Math.Floor(width * (k + 1));
            units.Add(new CopyUnit(plan, $"{key}#{k + 1}/{parts}", low, high, k == parts - 1));
        }
        return units;
    }

    private static async Task<long> CopyUnitAsync(CopyUnit unit, ColumnState[] states, string sqlServer, string postgres,
        DataCopyOptions options, Action<long> report, CancellationToken ct)
    {
        var plan = unit.Plan;
        string? where = null;
        string? pgWhere = null;
        if (unit.Low is long low && unit.High is long high && plan.SplitColumn is { } split)
        {
            string op = unit.LastRange ? "<=" : "<";
            where = $"[{split.Name.Replace("]", "]]")}] >= {ValueTransforms.Invariant(low)} AND [{split.Name.Replace("]", "]]")}] {op} {ValueTransforms.Invariant(high)}";
            var target = plan.Columns.First(c => c.Source == split);
            pgWhere = $"{NameMapper.Quote(target.TargetName)} >= {ValueTransforms.Invariant(low)} AND {NameMapper.Quote(target.TargetName)} {op} {ValueTransforms.Invariant(high)}";
        }

        await using var pg = new NpgsqlConnection(postgres);
        await pg.OpenAsync(ct);
        await ClearAsync(pg, plan, pgWhere, ct);

        await using var sql = new SqlConnection(sqlServer);
        await sql.OpenAsync(ct);
        await using var cmd = new SqlCommand(plan.SelectSql(where), sql) { CommandTimeout = options.CommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);

        var ordinals = new int[plan.Columns.Count];
        for (int i = 0, ordinal = 0; i < plan.Columns.Count; ordinal += plan.Columns[i].SourceSelect.Count, i++)
            ordinals[i] = ordinal;

        await using var importer = await pg.BeginBinaryImportAsync(plan.CopySql, ct);
        long rows = 0;
        while (await reader.ReadAsync(ct))
        {
            await importer.StartRowAsync(ct);
            for (int i = 0; i < plan.Columns.Count; i++)
            {
                var column = plan.Columns[i];
                object? value = column.Read(reader, ordinals[i], states[i]);
                if (value is null)
                    await importer.WriteNullAsync(ct);
                else
                    await importer.WriteAsync(value, column.DbType, ct);
            }
            if (++rows % ProgressEvery == 0)
                report(rows);
        }
        await importer.CompleteAsync(ct);
        return rows;
    }

    /// <summary>Empties the table (or the key range) so the copy starts clean, whatever an earlier run left.</summary>
    private static async Task ClearAsync(NpgsqlConnection pg, TablePlan plan, string? rangeWhere, CancellationToken ct)
    {
        string sql = rangeWhere is null ? $"DELETE FROM {plan.Target.Qualified}" : $"DELETE FROM {plan.Target.Qualified} WHERE {rangeWhere}";
        if (rangeWhere is null)
        {
            try
            {
                await using var truncate = new NpgsqlCommand($"TRUNCATE TABLE ONLY {plan.Target.Qualified}", pg);
                await truncate.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.FeatureNotSupported)
            {
                // Referenced by a foreign key: TRUNCATE is refused, DELETE is not.
            }
        }
        await using var delete = new NpgsqlCommand(sql, pg);
        await delete.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// After the copy: every identity column's sequence continues after the copied values (an
    /// empty table starts at its seed), every sequence continues after SQL Server's last value, and
    /// the copied tables are analyzed for the planner.
    /// </summary>
    private static async Task<IReadOnlyList<Diagnostic>> FinishAsync(SourceDatabase db, IReadOnlyList<TablePlan> plans, NameMapper names,
        string postgres, IReadOnlyDictionary<ObjectName, string> errors, CancellationToken ct)
    {
        var diagnostics = new List<Diagnostic>();
        await using var pg = new NpgsqlConnection(postgres);
        await pg.OpenAsync(ct);

        foreach (var plan in plans.Where(p => !errors.ContainsKey(p.Source.Name)))
        {
            foreach (var column in plan.Columns.Where(c => c.Source.Identity is not null))
            {
                var id = column.Source.Identity!;
                string col = NameMapper.Quote(column.TargetName);
                string pick = id.Increment >= 0 ? "max" : "min";
                string sql = $"SELECT setval(pg_get_serial_sequence({Literal(plan.Target.Qualified)}, {Literal(column.TargetName)}), "
                             + $"COALESCE({pick}({col}), {ValueTransforms.Invariant(id.Seed)}), {pick}({col}) IS NOT NULL) FROM {plan.Target.Qualified}";
                await ExecuteAsync(pg, sql, ct);
            }
            await ExecuteAsync(pg, $"ANALYZE {plan.Target.Qualified}", ct);
        }

        foreach (var sequence in db.Sequences.Where(s => s.CurrentValue is not null))
        {
            var name = names.Relation(sequence.Name);
            try
            {
                await ExecuteAsync(pg, $"SELECT setval({Literal(name.Qualified)}, {ValueTransforms.Invariant(sequence.CurrentValue!.Value)}, true)", ct);
            }
            catch (PostgresException ex)
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.TableCopyFailed, Severity.Error, sequence.Name,
                    $"Setting sequence {name.Qualified} to {sequence.CurrentValue} failed: {ex.MessageText}"));
            }
        }
        return diagnostics;
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task ExecuteAsync(NpgsqlConnection pg, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, pg);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The finished-units record behind resume, saved after every unit.</summary>
    private sealed class CopyState
    {
        private readonly string? _path;
        private readonly HashSet<string> _done;
        private readonly Lock _lock = new();

        private CopyState(string? path, HashSet<string> done)
        {
            _path = path;
            _done = done;
        }

        public static CopyState Load(string? path)
        {
            var done = new HashSet<string>(StringComparer.Ordinal);
            if (path is not null && File.Exists(path))
                done.UnionWith(JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path))?.Done ?? []);
            return new CopyState(path, done);
        }

        public bool IsDone(string key)
        {
            lock (_lock)
                return _done.Contains(key);
        }

        public void MarkDone(string key)
        {
            lock (_lock)
            {
                _done.Add(key);
                if (_path is null)
                    return;
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new StateFile(_done.Order(StringComparer.Ordinal).ToList()),
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
        }

        private sealed record StateFile(List<string> Done);
    }
}
