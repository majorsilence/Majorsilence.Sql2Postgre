using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// User-defined functions of every kind, created on SQL Server, converted and applied by sql2pg,
/// then called from queries on both engines: the rows must agree.
/// </summary>
[Category("Containers")]
public class UserFunctionBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10, 2) NOT NULL);
        GO
        INSERT INTO dbo.item VALUES (1, N'apple', 1.20), (2, N'bread', 3.50), (3, N'butter', 4.75), (4, N'cheese', 12.00), (5, N'banana', 0.80);
        GO
        CREATE FUNCTION dbo.leading_zeros(@value int) RETURNS varchar(8) AS
        BEGIN
            DECLARE @r varchar(8);
            SET @r = CONVERT(varchar(8), @value);
            SET @r = REPLICATE('0', 8 - LEN(@r)) + @r;
            RETURN (@r);
        END;
        GO
        CREATE FUNCTION dbo.padded_id(@id int) RETURNS varchar(10) AS BEGIN RETURN 'ID' + dbo.leading_zeros(@id); END;
        GO
        CREATE FUNCTION dbo.with_tax(@amount decimal(10, 2), @rate decimal(5, 4) = 0.2) RETURNS int AS BEGIN RETURN @amount * (1 + @rate); END;
        GO
        CREATE FUNCTION dbo.item_name(@id int) RETURNS nvarchar(50) AS
        BEGIN
            RETURN (SELECT name FROM dbo.item WHERE id = @id);
        END;
        GO
        CREATE FUNCTION dbo.fib(@n int) RETURNS bigint AS
        BEGIN
            DECLARE @a bigint = 0, @b bigint = 1, @i int = 0, @t bigint;
            WHILE @i < @n
            BEGIN
                SET @t = @a + @b;
                SET @a = @b;
                SET @b = @t;
                SET @i += 1;
            END
            RETURN @a;
        END;
        GO
        CREATE FUNCTION dbo.grade(@score int) RETURNS varchar(10) AS
        BEGIN
            IF @score >= 90 RETURN 'A';
            ELSE IF @score >= 70 RETURN 'B';
            RETURN 'C';
        END;
        GO
        CREATE FUNCTION dbo.names_list(@prefix nvarchar(10)) RETURNS nvarchar(400) AS
        BEGIN
            DECLARE @s nvarchar(400);
            SELECT @s = COALESCE(@s + N',', N'') + name FROM dbo.item WHERE name LIKE @prefix + N'%' ORDER BY id;
            RETURN @s;
        END;
        GO
        CREATE FUNCTION dbo.cheap_items(@max decimal(10, 2), @prefix nvarchar(10)) RETURNS TABLE AS
        RETURN SELECT id, name, price FROM dbo.item WHERE price <= @max AND name LIKE @prefix + N'%';
        GO
        CREATE FUNCTION dbo.price_bands(@step decimal(10, 2)) RETURNS @bands TABLE (band int NOT NULL PRIMARY KEY, items int NOT NULL, total decimal(12, 2) NULL) AS
        BEGIN
            INSERT INTO @bands (band, items, total)
            SELECT CAST(price / @step AS int), COUNT(*), SUM(price) FROM dbo.item GROUP BY CAST(price / @step AS int);
            UPDATE @bands SET total = total * 2 WHERE items > 1;
            IF NOT EXISTS (SELECT 1 FROM @bands)
                INSERT INTO @bands (band, items) VALUES (-1, 0);
            RETURN;
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "functions");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("functions");
        var schema = SchemaConverter.Convert(db, Options);
        var functions = schema.Objects.Where(o => o.Kind == ObjectKind.Function).ToList();
        Assert.That(functions.Select(o => $"{o.Source} {o.Status}"), Has.All.EndsWith(" Converted"),
            string.Join("\n", functions.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        var copy = await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString);
        Assert.That(copy.Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [TestCase("SELECT dbo.leading_zeros(42) AS a, dbo.leading_zeros(1234567) AS b, dbo.padded_id(7) AS c")]
    [TestCase("SELECT id, dbo.with_tax(price, 0.0825) AS t FROM dbo.item ORDER BY id")]
    [TestCase("SELECT dbo.item_name(2) AS found, dbo.item_name(99) AS missing")]
    [TestCase("SELECT dbo.fib(0) AS f0, dbo.fib(1) AS f1, dbo.fib(10) AS f10, dbo.fib(50) AS f50")]
    [TestCase("SELECT dbo.grade(95) AS a, dbo.grade(72) AS b, dbo.grade(10) AS c")]
    [TestCase("SELECT dbo.names_list(N'b') AS b, dbo.names_list(N'z') AS none")]
    [TestCase("SELECT id, name, price FROM dbo.cheap_items(4.00, N'b') ORDER BY id")]
    [TestCase("SELECT band, items, total FROM dbo.price_bands(5.00) ORDER BY band")]
    [TestCase("SELECT band, items FROM dbo.price_bands(1000.00)")]
    [TestCase("SELECT i.id, b.band, b.items FROM dbo.item AS i CROSS APPLY dbo.price_bands(i.price) AS b ORDER BY i.id, b.band")]
    [TestCase("SELECT name FROM dbo.item WHERE dbo.with_tax(price, DEFAULT) > 3 ORDER BY name")]
    public Task Same(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        return BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString);
    }
}
