using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Dynamic SQL converted by sql2pg and run on both engines: sp_executesql with parameters and an
/// OUTPUT parameter, names from variables through QUOTENAME, a value and a TOP spliced in, rows
/// returned through a refcursor, and a dynamic UPDATE.
/// </summary>
[Category("Containers")]
public class DynamicSqlBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int IDENTITY NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10, 2) NOT NULL);
        GO
        INSERT INTO dbo.item (name, price) VALUES (N'apple', 1.20), (N'bread', 3.50), (N'butter', 4.75), (N'cheese', 12.00), (N'banana', 0.80), (N'bread', 2.10);
        GO
        CREATE PROCEDURE dbo.count_cheaper @max decimal(10, 2), @count int OUTPUT AS
        BEGIN
            DECLARE @sql nvarchar(max) = N'SELECT @n = COUNT(*) FROM dbo.item WHERE price <= @limit';
            EXEC sp_executesql @sql, N'@limit decimal(10, 2), @n int OUTPUT', @limit = @max, @n = @count OUTPUT;
        END;
        GO
        CREATE PROCEDURE dbo.count_rows @schema sysname, @table sysname, @count int OUTPUT AS
        BEGIN
            DECLARE @sql nvarchar(max);
            SET @sql = N'SELECT @n = COUNT(*) FROM ' + QUOTENAME(@schema) + N'.' + QUOTENAME(@table);
            EXEC sp_executesql @sql, N'@n int OUTPUT', @n = @count OUTPUT;
        END;
        GO
        CREATE PROCEDURE dbo.find @name nvarchar(50), @top int AS
        BEGIN
            DECLARE @sql nvarchar(max) = N'SELECT TOP (' + CAST(@top AS nvarchar(10)) + N') id, name, price FROM dbo.item WHERE name = ''' + @name + N''' ORDER BY price DESC';
            EXEC (@sql);
        END;
        GO
        CREATE PROCEDURE dbo.raise_prices @percent int AS
        BEGIN
            EXEC sp_executesql N'UPDATE dbo.item SET price = price * (100 + @p) / 100 WHERE name LIKE N''b%''', N'@p int', @percent;
            SELECT name, price FROM dbo.item ORDER BY id;
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "dynamicsql");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _target = await PostgresDatabase.CreateAsync("dynamicsql");
        var schema = SchemaConverter.Convert(db, Options);
        var procedures = schema.Objects.Where(o => o.Kind == ObjectKind.Procedure).ToList();
        Assert.That(procedures.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", procedures.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
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

    /// <summary>OUTPUT parameters filled by dynamic SQL: SQL Server's from EXEC, PostgreSQL's from the CALL's INOUT row.</summary>
    [TestCase("DECLARE @c int; EXEC dbo.count_cheaper 4, @c OUTPUT; SELECT @c AS count", "CALL public.count_cheaper(4)")]
    [TestCase("DECLARE @c int; EXEC dbo.count_rows N'dbo', N'item', @c OUTPUT; SELECT @c AS count", "CALL public.count_rows('dbo', 'item')")]
    [TestCase("DECLARE @c int; EXEC dbo.count_rows N'dbo', N'ITEM', @c OUTPUT; SELECT @c AS count", "CALL public.count_rows('dbo', 'ITEM')")]
    public Task OutputParameters(string tsql, string pgsql) =>
        BothEngines.AssertSameResultAsync(tsql, pgsql, null, null, _source.ConnectionString, _target.ConnectionString);

    /// <summary>A dynamic SELECT's rows: SQL Server's result set, PostgreSQL's fetched from the cursor the CALL opened.</summary>
    [TestCase("EXEC dbo.find N'bread', 1", "CALL public.find('bread', 1, 'c1')")]
    [TestCase("EXEC dbo.find N'bread', 5", "CALL public.find('bread', 5, 'c1')")]
    public async Task ResultSet(string tsql, string call)
    {
        var expected = await BothEngines.RunSqlServerAsync(tsql, null, _source.ConnectionString);
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(call, conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await using var fetch = new NpgsqlCommand("FETCH ALL FROM c1", conn, tx);
        await using var reader = await fetch.ExecuteReaderAsync();
        var rows = new List<IReadOnlyList<string>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => CanonicalValue.Format(reader.GetValue(i), reader.GetDataTypeName(i))).ToList());
        Assert.That(rows, Is.EqualTo(expected.Rows));
    }

    /// <summary>
    /// A value with a quote spliced between quotes: SQL Server's string breaks (the value ends the
    /// literal), PostgreSQL's format() quotes the value with %L, so the query runs and finds nothing.
    /// </summary>
    [Test]
    public async Task QuotedValueIsEscaped()
    {
        await Assert.ThatAsync(() => BothEngines.RunSqlServerAsync("EXEC dbo.find N'O''Brien', 5", null, _source.ConnectionString), Throws.InstanceOf<Microsoft.Data.SqlClient.SqlException>());
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("CALL public.find('O''Brien', 5, 'c1')", conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await using var fetch = new NpgsqlCommand("FETCH ALL FROM c1", conn, tx);
        await using var reader = await fetch.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.False);
    }

    /// <summary>sp_executesql with a positional parameter in a procedure that also returns rows.</summary>
    [Test]
    public Task DynamicUpdate() =>
        BothEngines.AssertSameResultAsync("EXEC dbo.raise_prices 10", "SELECT * FROM public.raise_prices(10)", null, null, _source.ConnectionString, _target.ConnectionString);
}
