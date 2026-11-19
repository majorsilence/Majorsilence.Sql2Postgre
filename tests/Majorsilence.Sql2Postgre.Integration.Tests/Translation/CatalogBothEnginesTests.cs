using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Catalog queries on both engines: sys.* views, INFORMATION_SCHEMA views and the metadata
/// functions return the same SQL Server names and properties after conversion (ids are compared
/// only through functions, since PostgreSQL's are oids).
/// </summary>
[Category("Containers")]
public class CatalogBothEnginesTests
{
    private const string Script = """
        CREATE SCHEMA Sales;
        GO
        CREATE TABLE Sales.Customer (CustomerID int IDENTITY(1,1) NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Code char(4) NULL, Balance decimal(10,2) NULL);
        CREATE TABLE dbo.Item (ItemId int NOT NULL PRIMARY KEY, Title varchar(30) NULL, Notes nvarchar(max) NULL);
        GO
        CREATE VIEW Sales.CustomerNames AS SELECT CustomerID, Name FROM Sales.Customer;
        GO
        CREATE PROCEDURE dbo.ListItems AS SELECT ItemId FROM dbo.Item;
        GO
        CREATE FUNCTION dbo.Twice(@x int) RETURNS int AS BEGIN RETURN @x * 2 END;
        GO
        CREATE SEQUENCE Sales.OrderNumbers AS bigint START WITH 100 INCREMENT BY 5;
        GO
        CREATE PROCEDURE dbo.HasTable @schema sysname, @table sysname AS
            SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE SCHEMA_NAME(schema_id) = @schema AND name = @table) THEN 1 ELSE 0 END AS present;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "catalog");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("catalog");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        var before = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That(before.Failed, Is.Zero, string.Join("\n", before.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
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

    [TestCase("SELECT SCHEMA_NAME(schema_id) AS s, name, type, type_desc, is_ms_shipped FROM sys.objects WHERE type IN ('U', 'V', 'P', 'FN', 'SO') ORDER BY s, name")]
    [TestCase("SELECT name FROM sys.tables WHERE SCHEMA_NAME(schema_id) = N'Sales' ORDER BY name")]
    [TestCase("SELECT p.name FROM sys.procedures AS p JOIN sys.schemas AS s ON s.schema_id = p.schema_id WHERE s.name = 'dbo' ORDER BY p.name")]
    [TestCase("SELECT name FROM sys.views ORDER BY name")]
    [TestCase("SELECT t.name AS t, c.name AS c, c.column_id, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity FROM sys.tables AS t JOIN sys.columns AS c ON c.object_id = t.object_id ORDER BY t.name, c.column_id")]
    [TestCase("SELECT name FROM sys.schemas WHERE name IN ('dbo', 'Sales') ORDER BY name")]
    [TestCase("SELECT name, CAST(start_value AS bigint) AS s, CAST(increment AS bigint) AS i, CAST(current_value AS bigint) AS c FROM sys.sequences")]
    [TestCase("SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME")]
    [TestCase("SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'Sales' AND TABLE_NAME = 'Customer' ORDER BY ORDINAL_POSITION")]
    [TestCase("SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Item' ORDER BY ORDINAL_POSITION")]
    [TestCase("SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE FROM INFORMATION_SCHEMA.ROUTINES ORDER BY ROUTINE_NAME")]
    [TestCase("SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS")]
    [TestCase("SELECT CASE WHEN OBJECT_ID('Sales.Customer') IS NOT NULL THEN 1 ELSE 0 END AS a, CASE WHEN OBJECT_ID('Sales.Nope') IS NULL THEN 1 ELSE 0 END AS b, "
              + "CASE WHEN OBJECT_ID('Sales.Customer', 'V') IS NULL THEN 1 ELSE 0 END AS c, CASE WHEN OBJECT_ID(N'dbo.ListItems', N'P') IS NOT NULL THEN 1 ELSE 0 END AS d")]
    [TestCase("SELECT OBJECT_NAME(OBJECT_ID('dbo.Item')) AS a, OBJECT_NAME(OBJECT_ID(N'[Sales].[Customer]', N'U')) AS b, OBJECT_NAME(OBJECT_ID('Item')) AS c, SCHEMA_NAME(SCHEMA_ID('Sales')) AS d, SCHEMA_NAME() AS e")]
    [TestCase("SELECT COL_LENGTH('Sales.Customer', 'Name') AS a, COL_LENGTH('dbo.Item', 'Title') AS b, COL_LENGTH('dbo.Item', 'Notes') AS c, COL_LENGTH('Sales.Customer', 'Balance') AS d, COL_LENGTH('dbo.Item', 'Nope') AS e")]
    [TestCase("SELECT COLUMNPROPERTY(OBJECT_ID('Sales.Customer'), 'CustomerID', 'IsIdentity') AS a, COLUMNPROPERTY(OBJECT_ID('Sales.Customer'), 'Code', 'AllowsNull') AS b, COLUMNPROPERTY(OBJECT_ID('Sales.Customer'), 'Name', 'AllowsNull') AS c")]
    [TestCase("SELECT OBJECTPROPERTY(OBJECT_ID('Sales.CustomerNames'), 'IsView') AS a, OBJECTPROPERTY(OBJECT_ID('dbo.ListItems'), 'IsProcedure') AS b, OBJECTPROPERTY(OBJECT_ID('dbo.Item'), 'IsUserTable') AS c, OBJECTPROPERTY(OBJECT_ID('dbo.Item'), 'IsView') AS d, OBJECTPROPERTY(OBJECT_ID('dbo.Twice'), 'IsScalarFunction') AS e")]
    [TestCase("SELECT IS_ROLEMEMBER('db_owner') AS a, CASE WHEN DB_NAME(DB_ID()) = DB_NAME() THEN 1 ELSE 0 END AS b")]
    [TestCase("SELECT o.name FROM sys.objects AS o WHERE o.object_id = OBJECT_ID('dbo.Twice')")]
    [TestCase("EXEC dbo.HasTable N'Sales', N'Customer'")]
    [TestCase("EXEC dbo.HasTable N'dbo', N'Customer'")]
    public async Task SameResult(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(expected.Rows, Is.Not.Empty, "every case returns rows, so agreement is not two empty results");
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }
}
