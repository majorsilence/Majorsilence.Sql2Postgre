using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;
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
        var result = SchemaConverter.Convert(db, new ConversionOptions { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() });
        await AssertAppliesAsync(result);
    }

    [Test]
    public async Task ShopFixtureAppliesFromTheScript()
    {
        var db = SqlServerScriptReader.Read([("shop.sql", SqlServerDatabase.Fixture("shop.sql"))]).Database;
        var result = SchemaConverter.Convert(db, new ConversionOptions { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() });
        await AssertAppliesAsync(result);
    }

    /// <summary>
    /// SQL Server does not limit an index's included columns; PostgreSQL stores them in the btree
    /// row, which holds at most 2704 bytes. A 3800-character comment that does not compress (3800
    /// bytes in UTF-8) is copied into, and then inserted into, a table whose index includes it.
    /// </summary>
    [Test]
    public async Task LongIncludedValuesFitTheConvertedIndex()
    {
        var random = new Random(20261120);
        string Comment() => new(Enumerable.Range(0, 3800).Select(_ => (char)random.Next('!', '~' + 1)).ToArray());
        await using var source = await SqlServerDatabase.CreateAsync("""
            CREATE TABLE dbo.review (id int NOT NULL PRIMARY KEY, product_id int NOT NULL, comments nvarchar(3850) NULL);
            GO
            CREATE INDEX ix_review_product ON dbo.review (product_id) INCLUDE (comments);
            """, "include");
        await using (var sql = new SqlConnection(source.ConnectionString))
        {
            await sql.OpenAsync();
            await using var insert = new SqlCommand("INSERT INTO dbo.review VALUES (1, 7, @c)", sql);
            insert.Parameters.AddWithValue("@c", Comment());
            await insert.ExecuteNonQueryAsync();
        }
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        var options = new ConversionOptions { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
        var result = SchemaConverter.Convert(db, options);
        Assert.That(result.Objects.Single(o => o.Kind == ObjectKind.Index).Diagnostics.Select(d => d.Code), Does.Contain(DiagnosticCodes.IncludedColumnDropped));

        await using var target = await PostgresDatabase.CreateAsync("include");
        await PgApplier.ApplyAsync(result.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, options, source.ConnectionString, target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(result.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString);
        Assert.That(after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.SqlState} {o.Message}"), Is.Empty);

        await using var pg = new NpgsqlConnection(target.ConnectionString);
        await pg.OpenAsync();
        await using (var insert = new NpgsqlCommand("INSERT INTO public.review VALUES (2, 7, @c)", pg))
        {
            insert.Parameters.AddWithValue("c", Comment());
            await insert.ExecuteNonQueryAsync();
        }
        await using var count = new NpgsqlCommand("SELECT count(*) FROM public.review WHERE product_id = 7 AND length(comments) = 3800", pg);
        Assert.That(await count.ExecuteScalarAsync(), Is.EqualTo(2L));
    }

    private static async Task AssertAppliesAsync(ConversionResult result)
    {
        await using var target = await PostgresDatabase.CreateAsync();
        var report = await PgApplier.ApplyAsync(result.Objects.Where(o => o.Status != ObjectStatus.Manual), target.ConnectionString);
        Assert.That(report.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.SqlState} {o.Message}"), Is.Empty);
        Assert.That(result.Objects.Count(o => o.Kind == ObjectKind.Table && o.Status != ObjectStatus.Manual), Is.EqualTo(6));
    }
}
