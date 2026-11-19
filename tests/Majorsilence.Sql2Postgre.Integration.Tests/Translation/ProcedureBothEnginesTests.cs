using System.Data.Common;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Stored procedures created on SQL Server, converted and applied by sql2pg, then run on both
/// engines: procedures returning one result set are functions, others return their result sets
/// through refcursors, OUTPUT parameters and status codes come back through INOUT parameters.
/// </summary>
[Category("Containers")]
public class ProcedureBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int IDENTITY NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10, 2) NOT NULL);
        GO
        CREATE TABLE dbo.audit (id int IDENTITY NOT NULL PRIMARY KEY, note nvarchar(100) NOT NULL);
        GO
        INSERT INTO dbo.item (name, price) VALUES (N'apple', 1.20), (N'bread', 3.50), (N'butter', 4.75), (N'cheese', 12.00), (N'banana', 0.80);
        GO
        CREATE PROCEDURE dbo.add_item @name nvarchar(50), @price decimal(10, 2) = 0, @id int OUTPUT AS
        BEGIN
            SET NOCOUNT ON;
            IF @price < 0
                RETURN 1;
            INSERT INTO dbo.item (name, price) VALUES (@name, @price);
            SET @id = SCOPE_IDENTITY();
            RETURN 0;
        END;
        GO
        CREATE PROCEDURE dbo.try_add @name nvarchar(50), @price decimal(10, 2) AS
        BEGIN
            DECLARE @id int, @rc int;
            EXEC @rc = dbo.add_item @name, @price, @id OUTPUT;
            SELECT @rc AS rc, (SELECT name FROM dbo.item WHERE id = @id) AS added, (SELECT COUNT(*) FROM dbo.item) AS items;
        END;
        GO
        CREATE PROCEDURE dbo.items_under @max decimal(10, 2), @prefix nvarchar(10) = N'' AS
            SELECT id, name, price, price * 2 AS doubled FROM dbo.item WHERE price <= @max AND name LIKE @prefix + N'%' ORDER BY price DESC;
        GO
        CREATE PROCEDURE dbo.item_report @min decimal(10, 2) AS
        BEGIN
            SELECT COUNT(*) AS items, SUM(price) AS total FROM dbo.item WHERE price >= @min;
            SELECT id, name FROM dbo.item WHERE price >= @min ORDER BY id;
        END;
        GO
        CREATE PROCEDURE dbo.report_twice AS
        BEGIN
            EXEC dbo.item_report 4;
            SELECT N'done' AS status;
        END;
        GO
        CREATE PROCEDURE dbo.top_two AS
            SET ROWCOUNT 2;
            SELECT name, price FROM dbo.item ORDER BY price DESC;
            SET ROWCOUNT 0;
        GO
        CREATE PROCEDURE dbo.item_names @max decimal(10, 2) AS SELECT name FROM dbo.item WHERE price <= @max;
        GO
        CREATE PROCEDURE dbo.copy_cheap @max decimal(10, 2) AS
        BEGIN
            INSERT INTO dbo.audit (note) EXEC dbo.item_names @max;
            SELECT note FROM dbo.audit ORDER BY note;
        END;
        GO
        CREATE PROCEDURE dbo.double_it @value int OUTPUT AS SET @value = @value * 2;
        GO
        CREATE PROCEDURE dbo.doubles @start int AS
        BEGIN
            DECLARE @v int = @start, @w int = @start;
            EXEC dbo.double_it @v OUTPUT;
            EXEC dbo.double_it @w;
            EXEC dbo.double_it @value = @v OUTPUT;
            SELECT @v AS v, @w AS w;
        END;
        GO
        CREATE PROCEDURE dbo.restock @name nvarchar(50) AS
        BEGIN
            DECLARE @id int, @rc int;
            BEGIN TRAN;
            EXEC @rc = dbo.add_item @name, 5, @id OUTPUT;
            INSERT INTO dbo.audit (note) VALUES (N'restocked ' + @name);
            COMMIT TRAN;
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
        _source = await SqlServerDatabase.CreateAsync(Script, "procedures");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("procedures");
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

    /// <summary>A procedure converted to a function, called from a script: the rows agree.</summary>
    [TestCase("EXEC dbo.items_under 4.00")]
    [TestCase("EXEC dbo.items_under @max = 5, @prefix = N'b'")]
    [TestCase("EXEC dbo.try_add N'kiwi', 2.50")]
    [TestCase("EXEC dbo.try_add N'broken', -1")]
    [TestCase("EXEC dbo.top_two")]
    [TestCase("EXEC dbo.copy_cheap 2")]
    [TestCase("EXEC dbo.doubles 3")]
    [TestCase("INSERT INTO dbo.audit (note) EXEC dbo.item_names 2;\nSELECT note FROM dbo.audit ORDER BY note")]
    public Task Same(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        return BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString);
    }

    /// <summary>Several result sets: SQL Server's in turn, PostgreSQL's fetched from the cursors the CALL opened.</summary>
    [TestCase("EXEC dbo.item_report 3", "CALL public.item_report(3, 'c1', 'c2')", 2)]
    [TestCase("EXEC dbo.report_twice", "CALL public.report_twice('c1', 'c2', 'c3')", 3)]
    public async Task SameResultSets(string tsql, string call, int count)
    {
        var expected = new List<QueryResult>();
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            await using var cmd = new SqlCommand(tsql, conn, tx);
            await using var reader = await cmd.ExecuteReaderAsync();
            do
                expected.Add(await Read(reader));
            while (await reader.NextResultAsync());
        }

        var actual = new List<QueryResult>();
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand(call, conn, tx))
                await cmd.ExecuteNonQueryAsync();
            for (int k = 1; k <= count; k++)
            {
                await using var fetch = new NpgsqlCommand($"FETCH ALL FROM c{k}", conn, tx);
                await using var reader = await fetch.ExecuteReaderAsync();
                actual.Add(await Read(reader));
            }
            await tx.RollbackAsync();
        }

        Assert.That(actual, Has.Count.EqualTo(expected.Count));
        for (int k = 0; k < expected.Count; k++)
        {
            Assert.That(actual[k].Columns.Select(c => c.ToLowerInvariant()), Is.EqualTo(expected[k].Columns.Select(c => c.ToLowerInvariant())), $"result set {k + 1} columns");
            Assert.That(actual[k].Rows, Is.EqualTo(expected[k].Rows), $"result set {k + 1} rows");
        }
    }

    /// <summary>A procedure that commits runs outside an explicit transaction on both engines, with the same effect.</summary>
    [Test]
    public async Task CommitsLikeSqlServer()
    {
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("EXEC dbo.restock N'plum'", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("CALL public.restock('plum')", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await BothEngines.AssertSameResultAsync(
            "SELECT i.name, i.price, (SELECT COUNT(*) FROM dbo.audit WHERE note = N'restocked plum') AS notes FROM dbo.item AS i WHERE i.name = N'plum'",
            "SELECT i.name, i.price, (SELECT count(*) FROM public.audit WHERE note = 'restocked plum') AS notes FROM public.item AS i WHERE i.name = 'plum'",
            null, null, _source.ConnectionString, _target.ConnectionString);
    }

    private static async Task<QueryResult> Read(DbDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToList();
        var rows = new List<IReadOnlyList<string>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => CanonicalValue.Format(reader.GetValue(i), types[i])).ToList());
        return new QueryResult(columns, rows);
    }
}
