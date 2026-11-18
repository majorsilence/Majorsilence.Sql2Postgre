using DotNet.Testcontainers.Builders;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

/// <summary>
/// One SQL Server and one PostgreSQL container for the whole test run, started on first use and
/// stopped when the run ends. PostgreSQL's version comes from <c>SQL2PG_PG_VERSION</c> (default
/// 18). Without a reachable Docker engine every test that needs them is ignored, not failed.
/// </summary>
public static class Engines
{
    public const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    public static string PostgresVersion =>
        Environment.GetEnvironmentVariable("SQL2PG_PG_VERSION") is { Length: > 0 } v ? v : "18";

    private static readonly Lazy<Task<State>> Started = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record State(MsSqlContainer? SqlServer, PostgreSqlContainer? Postgres, string? Unavailable);

    /// <summary>SQL Server connection string (database <c>master</c>); ignores the test without Docker.</summary>
    public static async Task<string> SqlServerAsync() => (await RequireAsync()).SqlServer!.GetConnectionString();

    /// <summary>The SQL Server container itself (to copy backup files into it); ignores the test without Docker.</summary>
    public static async Task<MsSqlContainer> SqlServerContainerAsync() => (await RequireAsync()).SqlServer!;

    private static readonly Lazy<Task<(MsSqlContainer? Container, string? Unavailable)>> FullText =
        new(StartFullTextAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A second SQL Server, with Full-Text Search installed (the stock image has none), built from
    /// <c>Infrastructure/mssql-fts/Dockerfile</c> on first use; ignores the test without Docker.
    /// </summary>
    public static async Task<string> SqlServerFullTextAsync()
    {
        var (container, unavailable) = await FullText.Value;
        if (unavailable is not null)
            Assert.Ignore($"Docker is not available: {unavailable}");
        return container!.GetConnectionString();
    }

    private static async Task<(MsSqlContainer?, string?)> StartFullTextAsync()
    {
        try
        {
            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), Path.Combine("Infrastructure", "mssql-fts"))
                .WithName("sql2pg-mssql-fts:2022")
                .WithCleanUp(false)
                .Build();
            await image.CreateAsync();
            var container = new MsSqlBuilder(image.FullName).Build();
            await container.StartAsync();
            return (container, null);
        }
        catch (DockerUnavailableException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>PostgreSQL connection string; ignores the test without Docker.</summary>
    public static async Task<string> PostgresAsync() => (await RequireAsync()).Postgres!.GetConnectionString();

    private static async Task<State> RequireAsync()
    {
        var state = await Started.Value;
        if (state.Unavailable is not null)
            Assert.Ignore($"Docker is not available: {state.Unavailable}");
        return state;
    }

    private static async Task<State> StartAsync()
    {
        MsSqlContainer? sql = null;
        PostgreSqlContainer? pg = null;
        try
        {
            sql = new MsSqlBuilder(SqlServerImage).Build();
            pg = new PostgreSqlBuilder($"postgres:{PostgresVersion}").Build();
            await Task.WhenAll(sql.StartAsync(), pg.StartAsync());
            return new State(sql, pg, null);
        }
        catch (DockerUnavailableException ex)
        {
            return new State(null, null, ex.Message);
        }
    }

    internal static async Task StopAsync()
    {
        if (FullText.IsValueCreated && (await FullText.Value).Container is { } fullText)
            await fullText.DisposeAsync();
        if (!Started.IsValueCreated)
            return;
        var state = await Started.Value;
        if (state.SqlServer is not null)
            await state.SqlServer.DisposeAsync();
        if (state.Postgres is not null)
            await state.Postgres.DisposeAsync();
    }
}
