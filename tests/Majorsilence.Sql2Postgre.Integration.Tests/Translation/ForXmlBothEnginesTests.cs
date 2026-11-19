using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// FOR XML RAW, AUTO and PATH on both engines: the XML text each builds (attributes and elements,
/// NULL columns left out, escaping, bit as 1/0, ROOT, TYPE, no rows), compared as strings.
/// </summary>
[Category("Containers")]
public class ForXmlBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Item (ItemId int NOT NULL PRIMARY KEY, Price decimal(10,2) NOT NULL, Flag bit NOT NULL, Note nvarchar(40) NULL);
        GO
        INSERT INTO dbo.Item VALUES (1, 1.50, 1, NULL), (2, 2.25, 0, N'a&b<c "q"'), (3, 10.00, 1, N'plain');
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "forxml");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("forxml");
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

    [TestCase("SELECT ItemId, Price, Flag FROM dbo.Item ORDER BY ItemId FOR XML RAW")]
    [TestCase("SELECT ItemId, Price, Note FROM dbo.Item ORDER BY ItemId FOR XML RAW('item'), ELEMENTS")]
    [TestCase("SELECT ItemId, Note FROM dbo.Item ORDER BY ItemId DESC FOR XML RAW, ROOT('items')")]
    [TestCase("SELECT ItemId, Note FROM dbo.Item AS i ORDER BY ItemId FOR XML AUTO")]
    [TestCase("SELECT ItemId, Price FROM dbo.Item ORDER BY ItemId FOR XML AUTO, ELEMENTS, ROOT")]
    [TestCase("SELECT ItemId AS '@id', Price AS price, Note FROM dbo.Item ORDER BY ItemId FOR XML PATH('item'), ROOT('items')")]
    [TestCase("SELECT ItemId, Flag FROM dbo.Item ORDER BY ItemId FOR XML PATH")]
    [TestCase("SELECT ItemId AS '@id', Note AS 'text()' FROM dbo.Item ORDER BY ItemId FOR XML PATH('p')")]
    [TestCase("SELECT ItemId AS id FROM dbo.Item ORDER BY ItemId FOR XML PATH('')")]
    [TestCase("SELECT ItemId, Note FROM dbo.Item WHERE ItemId > 100 FOR XML RAW, ROOT('none')")]
    [TestCase("SELECT TOP (2) ItemId FROM dbo.Item ORDER BY Price DESC FOR XML RAW")]
    public async Task SameXml(string forXml) => await AssertSameAsync($"SELECT ({forXml}) AS x");

    [TestCase("SELECT CAST((SELECT ItemId, Note FROM dbo.Item ORDER BY ItemId FOR XML RAW, TYPE) AS nvarchar(max)) AS x")]
    [TestCase("SELECT i.ItemId, (SELECT i.Price AS '@p' FOR XML PATH('price')) AS x FROM dbo.Item AS i ORDER BY i.ItemId")]
    public async Task SameResult(string tsql) => await AssertSameAsync(tsql);

    private async Task AssertSameAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }
}
