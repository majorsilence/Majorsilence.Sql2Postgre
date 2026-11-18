using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

/// <summary>A throwaway database on the shared PostgreSQL container, dropped on dispose.</summary>
public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly string _maintenance;

    public string Name { get; }
    public string ConnectionString { get; }

    private PostgresDatabase(string maintenance, string name)
    {
        _maintenance = maintenance;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(maintenance) { Database = name }.ConnectionString;
    }

    public static async Task<PostgresDatabase> CreateAsync(string prefix = "target") =>
        await CreateAsync(prefix, await Engines.PostgresAsync());

    /// <summary>A database on the given server (<see cref="Engines.PostGisAsync"/>, say), as its maintenance database connects to it.</summary>
    public static async Task<PostgresDatabase> CreateAsync(string prefix, string maintenance)
    {
        var db = new PostgresDatabase(maintenance, $"{prefix}_{Guid.NewGuid():N}");
        await using var conn = new NpgsqlConnection(maintenance);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{db.Name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_maintenance);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
