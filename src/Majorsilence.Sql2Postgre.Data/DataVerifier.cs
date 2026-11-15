using System.IO.Hashing;
using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Data;

public enum VerifyMode { Count, Checksum }

/// <param name="ChecksumMatches">Null when only counts were compared.</param>
public sealed record TableVerification(ObjectName Table, string Target, long SourceRows, long TargetRows,
    bool? ChecksumMatches, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Passed => SourceRows == TargetRows && ChecksumMatches != false && Diagnostics.All(d => d.Severity != Severity.Error);
}

public sealed record VerificationResult(IReadOnlyList<TableVerification> Tables)
{
    public bool Passed => Tables.All(t => t.Passed);
}

/// <summary>
/// Checks that every copied table holds the same rows on both sides. Counts compare
/// <c>COUNT_BIG(*)</c> with <c>count(*)</c>. A checksum reads every row from both sides in any
/// order, turns each source value into what the copy makes of it (the copier's own transforms),
/// writes every value canonically (<see cref="CanonicalValue"/>), hashes each row with XxHash64,
/// and adds the row hashes modulo 2^64. Equal counts and sums mean the two multisets of rows agree;
/// the order-independence matters because the two engines sort text differently.
/// </summary>
public static class DataVerifier
{
    private const char FieldSeparator = '\u001f';

    public static async Task<VerificationResult> VerifyAsync(SourceDatabase db, ConversionOptions conversion,
        string sqlServerConnection, string postgresConnection, VerifyMode mode = VerifyMode.Count,
        IReadOnlyList<string>? tables = null, int maxParallel = 4, CancellationToken ct = default)
    {
        var translator = new SchemaTranslator(conversion);
        var plans = TablePlan.Build(db, translator.Translate(db), translator.Names)
            .Where(p => tables is null || tables.Count == 0 || tables.Any(f =>
                string.Equals(f, p.Source.Name.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(f, p.Source.Name.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var results = new TableVerification[plans.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, plans.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel), CancellationToken = ct },
            async (i, token) => results[i] = await VerifyTableAsync(plans[i], sqlServerConnection, postgresConnection, mode, token));
        return new VerificationResult(results);
    }

    private static async Task<TableVerification> VerifyTableAsync(TablePlan plan, string sqlServer, string postgres, VerifyMode mode, CancellationToken ct)
    {
        try
        {
            var (sourceRows, sourceSum) = mode == VerifyMode.Checksum
                ? await SourceChecksumAsync(plan, sqlServer, ct)
                : (await SourceCountAsync(plan, sqlServer, ct), 0UL);
            var (targetRows, targetSum) = mode == VerifyMode.Checksum
                ? await TargetChecksumAsync(plan, postgres, ct)
                : (await TargetCountAsync(plan, postgres, ct), 0UL);

            var diagnostics = new List<Diagnostic>();
            if (sourceRows != targetRows)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.RowCountMismatch, Severity.Error, plan.Source.Name,
                    $"{plan.Source.Name} has {sourceRows} rows in SQL Server and {targetRows} in PostgreSQL ({plan.Target.Qualified})."));
            bool? matches = mode == VerifyMode.Checksum ? sourceRows == targetRows && sourceSum == targetSum : null;
            if (matches == false && sourceRows == targetRows)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.ChecksumMismatch, Severity.Error, plan.Source.Name,
                    $"{plan.Source.Name} and {plan.Target.Qualified} have the same number of rows but different values."));
            return new TableVerification(plan.Source.Name, plan.Target.Qualified, sourceRows, targetRows, matches, diagnostics);
        }
        catch (Exception ex) when (ex is SqlException or NpgsqlException or InvalidCastException or OverflowException)
        {
            return new TableVerification(plan.Source.Name, plan.Target.Qualified, -1, -1, null,
                [new Diagnostic(DiagnosticCodes.TableCopyFailed, Severity.Error, plan.Source.Name, $"Verifying {plan.Source.Name} failed: {ex.Message}")]);
        }
    }

    private static async Task<long> SourceCountAsync(TablePlan plan, string sqlServer, CancellationToken ct)
    {
        await using var conn = new SqlConnection(sqlServer);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM {plan.SourceName}", conn) { CommandTimeout = 0 };
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> TargetCountAsync(TablePlan plan, string postgres, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(postgres);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {plan.Target.Qualified}", conn) { CommandTimeout = 0 };
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<(long Rows, ulong Sum)> SourceChecksumAsync(TablePlan plan, string sqlServer, CancellationToken ct)
    {
        await using var conn = new SqlConnection(sqlServer);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(plan.SelectSql(), conn) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
        var states = plan.Columns.Select(_ => new ColumnState()).ToArray();
        var values = new string[plan.Columns.Count];
        long rows = 0;
        ulong sum = 0;
        while (await reader.ReadAsync(ct))
        {
            for (int i = 0, ordinal = 0; i < plan.Columns.Count; ordinal += plan.Columns[i].SourceSelect.Count, i++)
                values[i] = CanonicalValue.Format(plan.Columns[i].Read(reader, ordinal, states[i]), plan.Columns[i].PgType);
            sum = unchecked(sum + RowHash(values));
            rows++;
        }
        return (rows, sum);
    }

    private static async Task<(long Rows, ulong Sum)> TargetChecksumAsync(TablePlan plan, string postgres, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(postgres);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {string.Join(", ", plan.Columns.Select(c => NameMapper.Quote(c.TargetName)))} FROM {plan.Target.Qualified}", conn) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
        var values = new string[plan.Columns.Count];
        long rows = 0;
        ulong sum = 0;
        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < plan.Columns.Count; i++)
                values[i] = CanonicalValue.Format(reader.GetValue(i), reader.GetDataTypeName(i));
            sum = unchecked(sum + RowHash(values));
            rows++;
        }
        return (rows, sum);
    }

    /// <summary>XxHash64 of the row's canonical values joined by U+001F, as UTF-8.</summary>
    public static ulong RowHash(IReadOnlyList<string> values) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(string.Join(FieldSeparator, values)));
}
