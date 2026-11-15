using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;

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
        var report = await PgApplier.ApplyAsync(result.Objects.Where(o => o.Status != ObjectStatus.Manual), target.ConnectionString);
        Assert.That(report.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.SqlState} {o.Message}"), Is.Empty);
        Assert.That(result.Objects.Count(o => o.Kind == ObjectKind.Table && o.Status != ObjectStatus.Manual), Is.EqualTo(6));
    }
}
