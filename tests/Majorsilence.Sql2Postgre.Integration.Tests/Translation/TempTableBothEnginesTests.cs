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
/// Temp tables and table variables in procedures and scripts, converted by sql2pg and run on
/// both engines. Each case runs twice in one session, and the second run's rows are compared:
/// a temporary table left behind by the first run would break the second.
/// </summary>
[Category("Containers")]
public class TempTableBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int IDENTITY NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10, 2) NOT NULL);
        GO
        CREATE TABLE dbo.plain (n int);
        GO
        CREATE TABLE dbo.note (id int IDENTITY NOT NULL PRIMARY KEY, text nvarchar(50) NOT NULL);
        GO
        INSERT INTO dbo.item (name, price) VALUES (N'apple', 1.20), (N'bread', 3.50), (N'butter', 4.75), (N'cheese', 12.00), (N'banana', 0.80);
        GO
        CREATE PROCEDURE dbo.cheap_items @max decimal(10, 2) AS
        BEGIN
            SET NOCOUNT ON;
            CREATE TABLE #cheap (id int NOT NULL PRIMARY KEY, name nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,
                price decimal(10, 2) CHECK (price >= 0), INDEX ix_name (name));
            INSERT INTO #cheap (id, name, price) SELECT id, name, price FROM dbo.item WHERE price <= @max;
            CREATE INDEX ix_price ON #cheap (price DESC);
            IF NOT EXISTS (SELECT 1 FROM #cheap)
                RETURN;
            SELECT c.name, c.price FROM #cheap AS c ORDER BY c.price;
        END;
        GO
        CREATE PROCEDURE dbo.price_changes @rise decimal(10, 2) AS
        BEGIN
            DECLARE @changes TABLE (seq int IDENTITY(1, 1), id int, old_price decimal(10, 2), new_price decimal(10, 2));
            UPDATE dbo.item SET price = price + @rise
            OUTPUT inserted.id, deleted.price, inserted.price INTO @changes (id, old_price, new_price)
            WHERE price < 2;
            SELECT id, old_price, new_price FROM @changes ORDER BY id;
        END;
        GO
        CREATE PROCEDURE dbo.identities AS
        BEGIN
            DECLARE @t TABLE (id int IDENTITY(5, 1), n int);
            CREATE TABLE #x (id int IDENTITY(50, 10), n int);
            DECLARE @a numeric(38, 0), @b numeric(38, 0), @c numeric(38, 0), @d numeric(38, 0);
            INSERT INTO @t (n) VALUES (1);
            INSERT INTO @t (n) VALUES (2);
            SET @a = SCOPE_IDENTITY();
            INSERT INTO #x (n) SELECT n FROM @t ORDER BY n;
            SET @b = SCOPE_IDENTITY();
            INSERT INTO #x (n) SELECT n FROM @t WHERE n > 5;
            SET @c = SCOPE_IDENTITY();
            INSERT INTO dbo.plain (n) VALUES (1);
            SET @d = SCOPE_IDENTITY();
            SELECT @a AS a, @b AS b, @c AS c, @d AS d;
        END;
        GO
        CREATE PROCEDURE dbo.loop_rows AS
        BEGIN
            DECLARE @i int = 0;
            WHILE @i < 3
            BEGIN
                DECLARE @seen TABLE (n int);
                INSERT INTO @seen (n) VALUES (@i);
                SET @i += 1;
            END;
            SELECT COUNT(*) AS seen FROM @seen;
        END;
        GO
        CREATE PROCEDURE dbo.inner_fill AS
        BEGIN
            DECLARE @t TABLE (n int);
            INSERT INTO @t (n) VALUES (100);
            INSERT INTO #shared (name) SELECT name FROM dbo.item WHERE price > 4;
        END;
        GO
        CREATE PROCEDURE dbo.outer_fill AS
        BEGIN
            DECLARE @t TABLE (n int);
            INSERT INTO @t (n) VALUES (1);
            CREATE TABLE #shared (name nvarchar(50));
            EXEC dbo.inner_fill;
            SELECT (SELECT COUNT(*) FROM @t) AS mine, (SELECT COUNT(*) FROM #shared) AS shared;
        END;
        GO
        CREATE PROCEDURE dbo.renumber AS
        BEGIN
            CREATE TABLE #n (id int IDENTITY(1, 1), name nvarchar(50));
            INSERT INTO #n (name) SELECT name FROM dbo.item ORDER BY id;
            TRUNCATE TABLE #n;
            INSERT INTO #n (name) SELECT name FROM dbo.item WHERE price > 3 ORDER BY id;
            SELECT id, name INTO #copy FROM #n;
            IF OBJECT_ID('tempdb..#copy') IS NOT NULL
                SELECT id, name FROM #copy ORDER BY id;
        END;
        GO
        CREATE PROCEDURE dbo.merge_prices AS
        BEGIN
            CREATE TABLE #log (action nvarchar(10), name nvarchar(50));
            MERGE dbo.item AS t
            USING (VALUES (N'apple', 2.00), (N'kiwi', 1.10)) AS s (name, price) ON t.name = s.name
            WHEN MATCHED THEN UPDATE SET price = s.price
            WHEN NOT MATCHED THEN INSERT (name, price) VALUES (s.name, s.price)
            OUTPUT $action, inserted.name INTO #log (action, name);
            SELECT action, name FROM #log ORDER BY name;
        END;
        GO
        CREATE PROCEDURE dbo.rollback_log AS
        BEGIN
            DECLARE @log TABLE (text nvarchar(50));
            BEGIN TRAN;
            INSERT INTO @log (text) VALUES (N'before');
            ROLLBACK;
            INSERT INTO @log (text) VALUES (N'after');
            INSERT INTO dbo.note (text) SELECT text FROM @log;
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };

    private static bool MergeOutputUnsupported => Engines.TargetVersion < 17;
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "temptables");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("temptables");
        var schema = SchemaConverter.Convert(db, Options);
        var procedures = schema.Objects.Where(o => o.Kind == ObjectKind.Procedure).ToList();
        if (MergeOutputUnsupported)
        {
            // MERGE … RETURNING is new in PostgreSQL 17: before it, merge_prices' MERGE … OUTPUT INTO is reported.
            var merge = procedures.Single(o => o.Source.Name == "merge_prices");
            Assert.That((merge.Status, merge.Diagnostics.Any(d => d.Code == DiagnosticCodes.OutputBefore18)), Is.EqualTo((ObjectStatus.Manual, true)));
            procedures.Remove(merge);
        }
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

    [TestCase("EXEC dbo.cheap_items 4.00")]
    [TestCase("EXEC dbo.cheap_items 0.10")]
    [TestCase("EXEC dbo.price_changes 0.50")]
    [TestCase("EXEC dbo.identities")]
    [TestCase("EXEC dbo.loop_rows")]
    [TestCase("EXEC dbo.outer_fill")]
    [TestCase("EXEC dbo.renumber")]
    [TestCase("EXEC dbo.merge_prices")]
    [TestCase("DECLARE @t TABLE (n int, name nvarchar(50));\nINSERT INTO @t (n, name) SELECT id, name FROM dbo.item WHERE price < 4;\nSELECT name FROM @t ORDER BY n;")]
    [TestCase("DECLARE @gone TABLE (id int);\nDELETE FROM dbo.plain OUTPUT deleted.n INTO @gone (id);\nINSERT INTO dbo.plain (n) VALUES (7), (8);\nTRUNCATE TABLE dbo.plain;\nSELECT COUNT(*) AS left_over FROM dbo.plain;")]
    public async Task SameOnTheSecondRun(string tsql)
    {
        if (MergeOutputUnsupported && tsql.Contains("merge_prices", StringComparison.Ordinal))
            Assert.Ignore("merge_prices has MERGE … OUTPUT, which needs PostgreSQL 17 (S2P4015).");
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);

        QueryResult expected;
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            expected = await TwiceAsync(() => new SqlCommand(tsql, conn, tx));
        }
        QueryResult actual;
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            actual = await TwiceAsync(() => new NpgsqlCommand(translation.Sql, conn, tx));
        }

        Assert.Multiple(() =>
        {
            // A procedure that returns before its SELECT sends no result set; the function it became returns no rows.
            if (expected.Columns.Count > 0)
                Assert.That(actual.Columns.Select(c => c.ToLowerInvariant()), Is.EqualTo(expected.Columns.Select(c => c.ToLowerInvariant())), "columns");
            Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
        });
    }

    /// <summary>
    /// ROLLBACK with a table variable: SQL Server keeps the variable's rows, PostgreSQL loses them
    /// (S2P5031) but creates the table again, so the procedure carries on.
    /// </summary>
    [Test]
    public async Task TableVariableSurvivesRollbackEmpty()
    {
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("EXEC dbo.rollback_log", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("CALL public.rollback_log()", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var sqlServer = await BothEngines.RunSqlServerAsync("SELECT text FROM dbo.note ORDER BY text", null, _source.ConnectionString);
        var postgres = await BothEngines.RunPostgresAsync("SELECT text FROM public.note ORDER BY text", null, _target.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(sqlServer.Rows.Select(r => r[0]), Is.EqualTo(new[] { "after", "before" }));
            Assert.That(postgres.Rows.Select(r => r[0]), Is.EqualTo(new[] { "after" }));
        });
    }

    /// <summary>Runs a command twice and reads the second run's first result set.</summary>
    private static async Task<QueryResult> TwiceAsync(Func<DbCommand> command)
    {
        await using (var first = command())
            await first.ExecuteNonQueryAsync();
        await using var second = command();
        await using var reader = await second.ExecuteReaderAsync();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToList();
        var rows = new List<IReadOnlyList<string>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => CanonicalValue.Format(reader.GetValue(i), types[i])).ToList());
        return new QueryResult(columns, rows);
    }
}
