using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

/// <summary>A throwaway database on the shared SQL Server container, built from a script and dropped on dispose.</summary>
public sealed class SqlServerDatabase : IAsyncDisposable
{
    private readonly string _master;

    public string Name { get; }
    public string ConnectionString { get; }

    private SqlServerDatabase(string master, string name)
    {
        _master = master;
        Name = name;
        ConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
    }

    public static async Task<SqlServerDatabase> CreateAsync(string script, string prefix = "fixture") =>
        await CreateAsync(script, prefix, await Engines.SqlServerAsync());

    /// <summary>A database on the given server (<see cref="Engines.SqlServerFullTextAsync"/>, say), as <c>master</c> connects to it.</summary>
    public static async Task<SqlServerDatabase> CreateAsync(string script, string prefix, string master)
    {
        var db = new SqlServerDatabase(master, $"{prefix}_{Guid.NewGuid():N}");
        await using (var conn = new SqlConnection(master))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"CREATE DATABASE [{db.Name}]", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var conn = new SqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (string batch in BatchSplitter.Split(script))
            {
                await using var cmd = new SqlCommand(batch, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        return db;
    }

    public static string Fixture(string fileName) =>
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", fileName));

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(_master);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
