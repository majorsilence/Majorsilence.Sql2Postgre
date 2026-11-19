using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// UPDATE/DELETE TOP (n) and UPDATE/DELETE whose target is inside an outer join, on both engines.
/// Each statement runs with a SELECT after it in one rolled-back transaction. TOP picks an
/// arbitrary n rows in both engines, so those cases compare counts.
/// </summary>
[Category("Containers")]
public class DmlFormsBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Item (ItemId int NOT NULL PRIMARY KEY, Price decimal(10,2) NOT NULL, Flag bit NOT NULL DEFAULT 0, Note nvarchar(20) NULL);
        CREATE TABLE dbo.Sale (SaleId int NOT NULL PRIMARY KEY, ItemId int NOT NULL, Qty int NOT NULL);
        GO
        INSERT INTO dbo.Item (ItemId, Price) VALUES (1, 1.50), (2, 2.50), (3, 3.50), (4, 4.50), (5, 5.50);
        INSERT INTO dbo.Sale VALUES (10, 1, 2), (11, 1, 1), (12, 3, 5), (13, 5, 1);
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "dmlforms");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("dmlforms");
        var schema = SchemaConverter.Convert(db, Options);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [TestCase("UPDATE TOP (2) dbo.Item SET Flag = 1 WHERE Price > 2;\nSELECT COUNT(*) AS n FROM dbo.Item WHERE Flag = 1")]
    [TestCase("UPDATE TOP (10) dbo.Item SET Flag = 1 WHERE Price > 2;\nSELECT COUNT(*) AS n FROM dbo.Item WHERE Flag = 1")]
    [TestCase("DELETE TOP (1) FROM dbo.Sale WHERE ItemId = 1;\nSELECT COUNT(*) AS n FROM dbo.Sale")]
    [TestCase("DELETE TOP (3) dbo.Sale;\nSELECT COUNT(*) AS n FROM dbo.Sale")]
    [TestCase("UPDATE TOP (1) i SET Flag = 1 FROM dbo.Item AS i JOIN dbo.Sale AS s ON s.ItemId = i.ItemId WHERE s.Qty >= 1;\nSELECT COUNT(*) AS n FROM dbo.Item WHERE Flag = 1")]
    [TestCase("DELETE i FROM dbo.Item AS i LEFT JOIN dbo.Sale AS s ON s.ItemId = i.ItemId WHERE s.SaleId IS NULL;\nSELECT ItemId FROM dbo.Item ORDER BY ItemId")]
    [TestCase("UPDATE i SET Flag = 1, Note = N'unsold' FROM dbo.Item AS i LEFT JOIN dbo.Sale AS s ON s.ItemId = i.ItemId WHERE s.SaleId IS NULL;\nSELECT ItemId, Flag, Note FROM dbo.Item ORDER BY ItemId")]
    [TestCase("UPDATE dbo.Item SET Price = Price * 2 FROM dbo.Item LEFT JOIN dbo.Sale ON dbo.Sale.ItemId = dbo.Item.ItemId WHERE dbo.Sale.Qty > 1 OR dbo.Sale.SaleId IS NULL;\nSELECT ItemId, Price FROM dbo.Item ORDER BY ItemId")]
    [TestCase("DELETE s FROM dbo.Item AS i RIGHT JOIN dbo.Sale AS s ON s.ItemId = i.ItemId WHERE i.Price > 3;\nSELECT SaleId FROM dbo.Sale ORDER BY SaleId")]
    [TestCase("DELETE TOP (1) i FROM dbo.Item AS i LEFT JOIN dbo.Sale AS s ON s.ItemId = i.ItemId WHERE s.SaleId IS NULL;\nSELECT COUNT(*) AS n FROM dbo.Item")]
    public async Task SameResult(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString);
    }

    /// <summary>SET that reads the outer-joined table cannot be picked by key alone: it stays reported.</summary>
    [Test]
    public void SetFromTheOuterJoinedTableIsReported()
    {
        var translation = ScriptTranslator.Translate(
            "UPDATE i SET Price = s.Qty FROM dbo.Item AS i LEFT JOIN dbo.Sale AS s ON s.ItemId = i.ItemId", _model, Options);
        Assert.That(translation.Diagnostics.Select(d => $"{d.Code} {d.Severity}"), Does.Contain($"{DiagnosticCodes.UpdateFromOuterJoin} Error"));
    }
}
