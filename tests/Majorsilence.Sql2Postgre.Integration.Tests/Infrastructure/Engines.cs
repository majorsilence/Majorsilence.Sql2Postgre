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

    /// <summary>
    /// <see cref="PostgresVersion"/> as the converter's <c>TargetVersion</c>: every container test
    /// converts for the PostgreSQL it runs on.
    /// </summary>
    public static int TargetVersion => int.Parse(PostgresVersion, System.Globalization.CultureInfo.InvariantCulture);

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

    private static readonly Lazy<Task<(PostgreSqlContainer? Container, string? Unavailable)>> PostGis =
        new(StartPostGisAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The PostGIS image for <see cref="PostgresVersion"/> (<c>postgis/postgis:18-3.6</c>), or null
    /// for a version postgis/postgis has no image of yet (19), which is built instead.
    /// </summary>
    public static string? PostGisImage => int.TryParse(PostgresVersion, out int v) && v >= 19 ? null : $"postgis/postgis:{PostgresVersion}-3.6";

    /// <summary>This file's folder, where the Dockerfiles are (the corpus tests link the file).</summary>
    private static string Infrastructure([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>
    /// A second PostgreSQL, with PostGIS available (the stock image has none), started on first use;
    /// ignores the test without Docker.
    /// </summary>
    public static async Task<string> PostGisAsync()
    {
        var (container, unavailable) = await PostGis.Value;
        if (unavailable is not null)
            Assert.Ignore($"Docker is not available: {unavailable}");
        return container!.GetConnectionString();
    }

    private static async Task<(PostgreSqlContainer?, string?)> StartPostGisAsync()
    {
        try
        {
            string image = PostGisImage ?? await BuildAsync("postgres-postgis", $"sql2pg-postgis:{PostgresVersion}");
            var container = new PostgreSqlBuilder(image).Build();
            await container.StartAsync();
            return (container, null);
        }
        catch (Exception ex) when (ex is DockerUnavailableException or DotNet.Testcontainers.Images.ImageBuildFailedException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Builds the image of <c>Infrastructure/{folder}/Dockerfile</c> for <see cref="PostgresVersion"/>.</summary>
    private static async Task<string> BuildAsync(string folder, string name)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(Infrastructure(), folder))
            .WithBuildArgument("PG_VERSION", PostgresVersion)
            .WithName(name)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();
        return image.FullName;
    }

    private static readonly Lazy<Task<(PostgreSqlContainer? Container, string? Unavailable)>> PgCron =
        new(StartPgCronAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The database pg_cron runs its jobs in on the pg_cron server (cron.database_name).</summary>
    public const string PgCronDatabase = "agentjobs";

    /// <summary>
    /// A PostgreSQL with pg_cron loaded, built from <c>Infrastructure/postgres-cron/Dockerfile</c>
    /// on first use; ignores the test without Docker, or when the image cannot be built.
    /// </summary>
    public static async Task<string> PgCronAsync()
    {
        var (container, unavailable) = await PgCron.Value;
        if (unavailable is not null)
            Assert.Ignore($"PostgreSQL with pg_cron is not available: {unavailable}");
        return container!.GetConnectionString();
    }

    private static async Task<(PostgreSqlContainer?, string?)> StartPgCronAsync()
    {
        try
        {
            string image = await BuildAsync("postgres-cron", $"sql2pg-postgres-cron:{PostgresVersion}");
            var container = new PostgreSqlBuilder(image)
                .WithCommand("-c", "shared_preload_libraries=pg_cron", "-c", $"cron.database_name={PgCronDatabase}")
                .Build();
            await container.StartAsync();
            return (container, null);
        }
        catch (Exception ex) when (ex is DockerUnavailableException or DotNet.Testcontainers.Images.ImageBuildFailedException)
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
        if (PostGis.IsValueCreated && (await PostGis.Value).Container is { } postGis)
            await postGis.DisposeAsync();
        if (PgCron.IsValueCreated && (await PgCron.Value).Container is { } pgCron)
            await pgCron.DisposeAsync();
        if (!Started.IsValueCreated)
            return;
        var state = await Started.Value;
        if (state.SqlServer is not null)
            await state.SqlServer.DisposeAsync();
        if (state.Postgres is not null)
            await state.Postgres.DisposeAsync();
    }
}
