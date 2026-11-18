using Majorsilence.Sql2Postgre.Compat;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// JSON on both engines: FOR JSON AUTO over joins (nesting by table, grouping consecutive rows,
/// an outer join with no match, ROOT, INCLUDE_NULL_VALUES), OPENJSON with and without WITH,
/// JSON_MODIFY (set, remove, append, strict, nested values) and ISJSON with a type. JSON results
/// are compared parsed, since the engines space JSON text differently.
/// </summary>
[Category("Containers")]
public class JsonBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Customer (Id int NOT NULL PRIMARY KEY, Name nvarchar(10) NOT NULL, City int NOT NULL, Phone nvarchar(10) NULL, Info nvarchar(200) NULL);
        GO
        CREATE TABLE dbo.City (Id int NOT NULL PRIMARY KEY, CityName nvarchar(10) NOT NULL);
        GO
        CREATE TABLE dbo.Contact (CustomerId int NOT NULL, ContactName nvarchar(10) NOT NULL);
        GO
        CREATE TABLE dbo.Reading (Rego nvarchar(10) NOT NULL, Sensor int NOT NULL, RecordedWhen datetime2 NOT NULL, Temperature decimal(18, 2) NOT NULL, Full_ nvarchar(max) NOT NULL);
        GO
        INSERT INTO dbo.Customer VALUES (1, N'Ann', 10, N'111', N'{"Tags":["a"]}'), (2, N'Bob', 10, NULL, N'{"Tags":[]}'), (3, N'Cy', 20, N'333', NULL);
        INSERT INTO dbo.City VALUES (10, N'Oslo'), (20, N'Rome');
        INSERT INTO dbo.Contact VALUES (1, N'pa'), (1, N'pb'), (3, N'pc');
        GO
        CREATE PROCEDURE dbo.RecordReadings @doc nvarchar(max) AS
        BEGIN
            SET NOCOUNT ON;
            IF ISJSON(@doc) = 0
                THROW 51000, N'not JSON', 1;
            INSERT INTO dbo.Reading (Rego, Sensor, RecordedWhen, Temperature, Full_)
            SELECT Rego, Sensor, RecordedWhen, Temperature, Full_
            FROM OPENJSON(@doc, N'$.Recordings')
            WITH (Rego nvarchar(10) N'$.properties.rego', Sensor int N'$.properties.sensor', RecordedWhen datetime2(7) N'$.properties.when',
                  Temperature decimal(18, 2) N'$.properties.temp', Full_ nvarchar(max) N'$' AS JSON);
        END;
        GO
        CREATE PROCEDURE dbo.Search @text nvarchar(10), @rows int AS
        BEGIN
            SELECT TOP (@rows) c.Id, c.Name, ct.CityName, c.Phone, p.ContactName
            FROM dbo.Customer AS c JOIN dbo.City AS ct ON ct.Id = c.City LEFT JOIN dbo.Contact AS p ON p.CustomerId = c.Id
            WHERE c.Name LIKE N'%' + @text + N'%'
            ORDER BY c.Name, p.ContactName
            FOR JSON AUTO, ROOT(N'Customers');
        END;
        GO
        CREATE PROCEDURE dbo.Tag @id int, @tag nvarchar(20) AS
        BEGIN
            SET NOCOUNT ON;
            UPDATE dbo.Customer SET Info = JSON_MODIFY(Info, N'append $.Tags', @tag) WHERE Id = @id;
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "json");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("json");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS mssql_compat;\n"
            + string.Concat(CompatScripts.Names.Select(n => CompatScripts.Create(n, "mssql_compat") + "\n")), conn).ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [TestCase("SELECT c.Id, c.Name, t.CityName, c.Phone, p.ContactName FROM dbo.Customer AS c JOIN dbo.City AS t ON t.Id = c.City LEFT JOIN dbo.Contact AS p ON p.CustomerId = c.Id ORDER BY c.Id, p.ContactName FOR JSON AUTO")]
    [TestCase("SELECT c.Id, t.CityName FROM dbo.Customer AS c JOIN dbo.City AS t ON t.Id = c.City ORDER BY c.Id FOR JSON AUTO, ROOT(N'Customers')")]
    [TestCase("SELECT t.CityName, c.Name FROM dbo.Customer AS c JOIN dbo.City AS t ON t.Id = c.City ORDER BY t.CityName, c.Name FOR JSON AUTO")]
    [TestCase("SELECT c.Id, p.ContactName FROM dbo.Customer AS c LEFT JOIN dbo.Contact AS p ON p.CustomerId = c.Id ORDER BY c.Id FOR JSON AUTO, INCLUDE_NULL_VALUES")]
    [TestCase("SELECT c.Id, t.CityName, c.Id + 100 AS k FROM dbo.Customer AS c JOIN dbo.City AS t ON t.Id = c.City ORDER BY c.Id FOR JSON AUTO")]
    [TestCase("SELECT TOP (2) c.Id, City.CityName FROM dbo.Customer AS c JOIN dbo.City ON City.Id = c.City ORDER BY c.Name DESC FOR JSON AUTO")]
    [TestCase("EXEC dbo.Search N'', 3")]
    [TestCase("EXEC dbo.Search N'y', 5")]
    [TestCase("SELECT JSON_MODIFY(N'{\"a\":1}', N'$.b', N'x') AS a, JSON_MODIFY(N'{\"a\":1,\"b\":2}', N'$.a', NULL) AS b, JSON_MODIFY(N'{\"a\":1}', N'strict $.a', NULL) AS c, JSON_MODIFY(N'{\"e\":[]}', N'append $.e', N'v') AS d")]
    [TestCase("SELECT JSON_MODIFY(N'{\"e\":[]}', N'append $.e', JSON_QUERY(N'{\"k\":1}')) AS a, JSON_MODIFY(N'{\"e\":[]}', N'append $.e', JSON_MODIFY(N'{}', N'$.a', N'x')) AS b, JSON_MODIFY(N'{}', N'append $.e', N'v') AS c, JSON_MODIFY(N'{\"a\":{\"b\":1}}', N'$.a.c', N'y') AS d")]
    [TestCase("SELECT JSON_MODIFY(N'{}', N'$.x.y', N'z') AS a, JSON_MODIFY(N'{\"a\":1}', N'$.a', CAST(2.50 AS decimal(5, 2))) AS b, JSON_MODIFY(N'{\"a\":[1,2]}', N'$.a[1]', N'z') AS c, JSON_MODIFY(N'{\"a\":[1,2]}', N'$.a[5]', N'z') AS d, JSON_MODIFY(N'[1,2,3]', N'$[1]', NULL) AS e, JSON_MODIFY(N'{\"a\":\"x\"}', N'$.a', N'q\"uote') AS f")]
    [TestCase("SELECT Id, JSON_MODIFY(JSON_MODIFY(Info, N'$.n', Id), N'append $.Tags', Name) AS v FROM dbo.Customer ORDER BY Id")]
    public async Task SameJson(string tsql) => await AssertSameAsync(tsql, json: true);

    [TestCase("SELECT [key], [value], [type] FROM OPENJSON(N'{\"a\":1,\"b\":\"x\",\"c\":null,\"d\":true,\"e\":[1,2],\"f\":{\"g\":1},\"h\":1.50}')")]
    [TestCase("SELECT [key], [value], [type] FROM OPENJSON(N'[10,\"y\",{\"z\":1}]')")]
    [TestCase("SELECT [key], [value] FROM OPENJSON(N'{\"a\":\"x\\\"y\",\"b\":-0.5e1}')")]
    [TestCase("SELECT * FROM OPENJSON(N'{\"r\":[{\"p\":{\"x\":1,\"w\":\"2016-01-01T07:00:00\"},\"q\":\"a\"},{\"p\":{\"x\":\"7\"}}]}', N'$.r') WITH (x int N'$.p.x', w datetime2 N'$.p.w', q nvarchar(5), p nvarchar(max) AS JSON)")]
    [TestCase("SELECT * FROM OPENJSON(N'{\"p\":{\"x\":1}}') WITH (x int N'$.p.x', p nvarchar(10) N'$.p')")]
    [TestCase("SELECT c.Id, j.[value] FROM dbo.Customer AS c CROSS APPLY OPENJSON(c.Info, N'$.Tags') AS j ORDER BY c.Id, j.[key]")]
    [TestCase("SELECT ISJSON(N'{}') AS a, ISJSON(N'[1]') AS b, ISJSON(N'1') AS c, ISJSON(N'x') AS d, ISJSON(NULL) AS e, ISJSON(N'1', SCALAR) AS f, ISJSON(N'[1]', OBJECT) AS g, ISJSON(N'[1]', ARRAY) AS h, ISJSON(N'{}', VALUE) AS i, ISJSON(N' {} ') AS j")]
    public async Task SameRows(string tsql) => await AssertSameAsync(tsql);

    [Test]
    public async Task ProceduresReadAndChangeJson()
    {
        const string doc = """{"Recordings":[{"type":"Feature","properties":{"rego":"WWI-321-A","sensor":1,"when":"2016-01-01T07:00:00","temp":3.96}},{"type":"Feature","properties":{"rego":"WWI-321-B","sensor":2,"when":"2016-01-01T07:05:00","temp":3.98}}]}""";
        foreach (var tsql in new[] { $"EXEC dbo.RecordReadings N'{doc}'", "EXEC dbo.Tag 1, N'new'", "EXEC dbo.Tag 2, N'first'" })
        {
            var translation = ScriptTranslator.Translate(tsql, _model, Options);
            Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
            await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
            await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        }
        await AssertSameAsync("SELECT Rego, Sensor, RecordedWhen, Temperature, Full_ FROM dbo.Reading ORDER BY Sensor", json: true);
        await AssertSameAsync("SELECT Id, Info FROM dbo.Customer ORDER BY Id", json: true);
    }

    private async Task AssertSameAsync(string tsql, bool json = false)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString, json);
    }
}
