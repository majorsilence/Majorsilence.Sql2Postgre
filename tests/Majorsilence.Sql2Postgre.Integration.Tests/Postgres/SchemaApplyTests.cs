using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Postgres;

/// <summary>
/// The shop fixture, created on SQL Server, read from its catalog, converted and applied to the
/// PostgreSQL under test: every object that is not Manual or Skipped creates without error.
/// (The test PostgreSQL has no extensions, so they are disabled.)
/// </summary>
[Category("Containers")]
public class SchemaApplyTests
{
    [Test]
    public async Task ShopFixtureAppliesFromTheCatalog()
    {
        await using var source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql"), "shop");
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        var result = SchemaConverter.Convert(db, new ConversionOptions { Extensions = new HashSet<string>() });
        await AssertAppliesAsync(result);
    }

    [Test]
    public async Task ShopFixtureAppliesFromTheScript()
    {
        var db = SqlServerScriptReader.Read([("shop.sql", SqlServerDatabase.Fixture("shop.sql"))]).Database;
        var result = SchemaConverter.Convert(db, new ConversionOptions { Extensions = new HashSet<string>() });
        await AssertAppliesAsync(result);
    }

    private static async Task AssertAppliesAsync(ConversionResult result)
    {
        await using var target = await PostgresDatabase.CreateAsync();
        var failures = new List<string>();
        await using var conn = new NpgsqlConnection(target.ConnectionString);
        await conn.OpenAsync();
        foreach (var o in result.Objects.Where(o => o.Status is not (ObjectStatus.Manual or ObjectStatus.Skipped)))
        {
            try
            {
                await using var cmd = new NpgsqlCommand(o.Sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex)
            {
                failures.Add($"{o.TargetName}: {ex.SqlState} {ex.MessageText}\n{o.Sql}");
            }
        }
        Assert.That(failures, Is.Empty);
        Assert.That(result.Objects.Count(o => o.Kind == ObjectKind.Table && o.Status != ObjectStatus.Manual), Is.EqualTo(6));
    }
}
