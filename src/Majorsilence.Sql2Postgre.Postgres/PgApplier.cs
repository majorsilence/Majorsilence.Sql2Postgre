using Majorsilence.Sql2Postgre.Model;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Postgres;

public enum ApplyStatus { Applied, Failed, Skipped }

/// <param name="SqlState">PostgreSQL's error code when the object failed (<c>42P07</c>, …).</param>
public sealed record ApplyOutcome(ObjectKind Kind, string TargetName, Phase Phase, ApplyStatus Status,
    string? SqlState = null, string? Message = null);

public sealed record ApplyReport(IReadOnlyList<ApplyOutcome> Outcomes)
{
    public int Applied => Outcomes.Count(o => o.Status == ApplyStatus.Applied);
    public int Failed => Outcomes.Count(o => o.Status == ApplyStatus.Failed);
    public int Skipped => Outcomes.Count(o => o.Status == ApplyStatus.Skipped);
}

public sealed record ApplyOptions
{
    /// <summary>Stop at the first object that fails instead of recording it and going on.</summary>
    public bool StopOnError { get; init; }

    /// <summary>
    /// Also apply Manual views, routines and triggers. Manual tables, constraints and indexes are
    /// always applied: their DDL is complete with the untranslatable part commented out, and
    /// leaving a table out would fail everything that references it.
    /// </summary>
    public bool ApplyManual { get; init; }
}

/// <summary>
/// Runs converted objects against a PostgreSQL database, each in its own transaction, recording
/// what applied and what failed (with PostgreSQL's SQLSTATE). Skipped objects are never run.
/// </summary>
public static class PgApplier
{
    private sealed record Unit(ObjectKind Kind, string TargetName, Phase Phase, ObjectStatus Status, string Sql);

    public static Task<ApplyReport> ApplyAsync(IEnumerable<ConvertedObject> objects, string connectionString,
        ApplyOptions? options = null, CancellationToken ct = default) =>
        RunAsync(objects.Select(o => new Unit(o.Kind, o.TargetName, o.Phase, o.Status, o.Sql)), connectionString, options ?? new(), ct);

    /// <summary>Applies a directory written by <see cref="ScriptPlanner.WriteFiles"/>, split on its marker lines.</summary>
    public static Task<ApplyReport> ApplyDirectoryAsync(string directory, string connectionString,
        ApplyOptions? options = null, CancellationToken ct = default) =>
        RunAsync(ScriptPlanner.ReadScripts(directory).Select(u => new Unit(u.Kind, u.TargetName, PhaseOf(u.File), u.Status, u.Sql)),
            connectionString, options ?? new(), ct);

    private static Phase PhaseOf(string file) =>
        Enum.GetValues<Phase>().FirstOrDefault(p => ScriptPlanner.FileName(p) == file);

    private static async Task<ApplyReport> RunAsync(IEnumerable<Unit> units, string connectionString, ApplyOptions options, CancellationToken ct)
    {
        var outcomes = new List<ApplyOutcome>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        bool stopped = false;
        foreach (var u in units)
        {
            if (stopped || u.Status == ObjectStatus.Skipped || string.IsNullOrWhiteSpace(u.Sql)
                || (u.Status == ObjectStatus.Manual && u.Phase is Phase.Code or Phase.Triggers && !options.ApplyManual))
            {
                outcomes.Add(new ApplyOutcome(u.Kind, u.TargetName, u.Phase, ApplyStatus.Skipped));
                continue;
            }
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using var cmd = new NpgsqlCommand(u.Sql, conn, tx);
                await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                outcomes.Add(new ApplyOutcome(u.Kind, u.TargetName, u.Phase, ApplyStatus.Applied));
            }
            catch (PostgresException ex)
            {
                await tx.RollbackAsync(ct);
                outcomes.Add(new ApplyOutcome(u.Kind, u.TargetName, u.Phase, ApplyStatus.Failed, ex.SqlState, ex.MessageText));
                stopped = options.StopOnError;
            }
        }
        return new ApplyReport(outcomes);
    }

    /// <summary>
    /// Drops and recreates the target database (through the <c>postgres</c> maintenance database
    /// on the same server). Destructive: only when the user asks for it.
    /// </summary>
    public static async Task RecreateDatabaseAsync(string connectionString, CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        string name = target.Database ?? throw new ArgumentException("The connection string names no database.", nameof(connectionString));
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(maintenance.ConnectionString);
        await conn.OpenAsync(ct);
        string quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
        foreach (string sql in new[] { $"DROP DATABASE IF EXISTS {quoted} WITH (FORCE)", $"CREATE DATABASE {quoted}" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
