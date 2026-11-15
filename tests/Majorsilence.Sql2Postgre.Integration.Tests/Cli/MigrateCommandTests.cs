using Majorsilence.Sql2Postgre.Cli;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Cli;

[Category("Containers")]
public class MigrateCommandTests
{
    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await new Sql2PgCli(output, error).InvokeAsync(args);
        return (code, output.ToString(), error.ToString());
    }

    private static string NoExtensionsConfig()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sql2pg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "extensions": [] }""");
        return path;
    }

    [Test]
    public async Task ShopFixtureMigratesEndToEnd()
    {
        await using var source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + ShopData.EdgeRows, "migrate");
        await using var target = await PostgresDatabase.CreateAsync("migrate");
        var (code, output, error) = await Run("migrate", "--source", source.ConnectionString, "--target", target.ConnectionString,
            "--config", NoExtensionsConfig(), "--verify", "checksum");

        Assert.Multiple(() =>
        {
            // The fixture has a procedure created WITH ENCRYPTION, which needs a person: exit code 1.
            Assert.That(code, Is.EqualTo(ExitCodes.CompletedWithProblems), error);
            Assert.That(output, Does.Contain("S2P0101"));
            Assert.That(output, Does.Not.Contain("FAILED"));
            Assert.That(output, Does.Match(@"All \d+ tables verified\."));
            Assert.That(output + error, Does.Not.Contain(new Npgsql.NpgsqlConnectionStringBuilder(target.ConnectionString).Password!));
        });

        await using var pg = new NpgsqlConnection(target.ConnectionString);
        await pg.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM shop.all_types), (SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND connamespace = 'shop'::regnamespace)", pg);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.That((reader.GetInt64(0), reader.GetInt64(1)), Is.EqualTo((6L, 4L)), "rows copied, foreign keys created after the data");
    }

    [Test]
    public async Task CleanDatabaseMigratesWithExitCodeZero()
    {
        await using var source = await SqlServerDatabase.CreateAsync("""
            CREATE TABLE dbo.Item (ItemID int IDENTITY PRIMARY KEY, Name nvarchar(50) NOT NULL, Price money NOT NULL DEFAULT 0);
            GO
            INSERT INTO dbo.Item (Name, Price) VALUES (N'one', 1.5), (N'two', 2.25);
            """, "clean");
        await using var target = await PostgresDatabase.CreateAsync("clean");
        var (code, output, error) = await Run("migrate", "--source", source.ConnectionString, "--target", target.ConnectionString);
        Assert.That(code, Is.EqualTo(ExitCodes.Success), output + error);
        Assert.That(output, Does.Contain("All 1 tables verified."));
    }

    [Test]
    public async Task SchemaThenApplyFromScripts()
    {
        await using var source = await SqlServerDatabase.CreateAsync("CREATE TABLE dbo.t (id int NOT NULL PRIMARY KEY);", "two-step");
        await using var target = await PostgresDatabase.CreateAsync("two-step");
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"scripts-{Guid.NewGuid():N}");
        var schema = await Run("schema", "--source", source.ConnectionString, "--out", dir);
        var apply = await Run("apply", "--scripts", dir, "--target", target.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(schema.Code, Is.EqualTo(ExitCodes.Success), schema.Err);
            Assert.That(apply.Code, Is.EqualTo(ExitCodes.Success), apply.Out + apply.Err);
            Assert.That(apply.Out, Does.Contain("Applied 2, failed 0"));
        });
        Directory.Delete(dir, recursive: true);
    }
}
