using System.Data.Common;
using Majorsilence.Sql2Postgre.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

/// <summary>The rows a query returned, as canonical strings (see <see cref="CanonicalValue"/>).</summary>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Runs a T-SQL query on SQL Server and its PostgreSQL translation on PostgreSQL and asserts the
/// two return the same thing. Each side runs inside a transaction that is rolled back, so setup
/// DDL and data never leak into the next test. Rows are compared as multisets unless the T-SQL
/// query has a top-level <c>ORDER BY</c>, then as sequences; column names case-insensitively.
/// </summary>
public static class BothEngines
{
    public static async Task AssertSameResultAsync(string tsql, string pgsql,
        string? tsqlSetup = null, string? pgSetup = null)
    {
        var sqlServer = await RunSqlServerAsync(tsql, tsqlSetup);
        var postgres = await RunPostgresAsync(pgsql, pgSetup);
        bool ordered = HasTopLevelOrderBy(tsql);

        Assert.Multiple(() =>
        {
            Assert.That(postgres.Columns.Select(c => c.ToLowerInvariant()),
                Is.EqualTo(sqlServer.Columns.Select(c => c.ToLowerInvariant())), "Column names");
            Assert.That(Rows(postgres, ordered), Is.EqualTo(Rows(sqlServer, ordered)),
                ordered ? "Rows (in order)" : "Rows (as a multiset)");
        });
    }

    public static async Task<QueryResult> RunSqlServerAsync(string sql, string? setup = null)
    {
        await using var conn = new SqlConnection(await Engines.SqlServerAsync());
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        var result = await RunAsync(conn, tx, sql, setup, (text, c, t) => new SqlCommand(text, (SqlConnection)c, (SqlTransaction)t));
        await tx.RollbackAsync();
        return result;
    }

    public static async Task<QueryResult> RunPostgresAsync(string sql, string? setup = null)
    {
        await using var conn = new NpgsqlConnection(await Engines.PostgresAsync());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var result = await RunAsync(conn, tx, sql, setup, (text, c, t) => new NpgsqlCommand(text, (NpgsqlConnection)c, (NpgsqlTransaction)t));
        await tx.RollbackAsync();
        return result;
    }

    private static async Task<QueryResult> RunAsync(DbConnection conn, DbTransaction tx, string sql, string? setup,
        Func<string, DbConnection, DbTransaction, DbCommand> command)
    {
        if (!string.IsNullOrWhiteSpace(setup))
        {
            await using var setupCmd = command(setup, conn, tx);
            await setupCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = command(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToList();
        var rows = new List<IReadOnlyList<string>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(i => CanonicalValue.Format(reader.GetValue(i), types[i])).ToList());
        return new QueryResult(columns, rows);
    }

    private static List<string> Rows(QueryResult result, bool ordered)
    {
        var rows = result.Rows.Select(r => string.Join('\u001f', r)).ToList();
        if (!ordered)
            rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>Whether the script's last statement is a SELECT with an ORDER BY of its own.</summary>
    public static bool HasTopLevelOrderBy(string tsql)
    {
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(tsql), out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
            return false;
        var last = script.Batches.LastOrDefault()?.Statements.LastOrDefault();
        return last is SelectStatement { QueryExpression.OrderByClause: not null };
    }
}
