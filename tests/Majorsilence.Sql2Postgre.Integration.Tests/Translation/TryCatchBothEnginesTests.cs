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
/// TRY … CATCH converted by sql2pg and run on both engines: the error numbers ERROR_NUMBER()
/// reports, THROW and RAISERROR messages, nested handlers, and the transaction patterns (BEGIN
/// TRAN in or before TRY, ROLLBACK in CATCH) with the data each leaves committed.
/// </summary>
[Category("Containers")]
public class TryCatchBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int IDENTITY NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL UNIQUE, price decimal(10, 2) NOT NULL CHECK (price >= 0));
        GO
        CREATE TABLE dbo.error_log (id int IDENTITY NOT NULL PRIMARY KEY, number int, message nvarchar(4000), procedure_name sysname NULL);
        GO
        INSERT INTO dbo.item (name, price) VALUES (N'apple', 1.20), (N'bread', 3.50);
        GO
        CREATE PROCEDURE dbo.error_info @kind int AS
        BEGIN
            DECLARE @number int = 0, @message nvarchar(4000) = N'none', @x int, @proc sysname;
            BEGIN TRY
                IF @kind = 1 SET @x = 1 / 0;
                IF @kind = 2 THROW 50010, N'custom error', 1;
                IF @kind = 3 RAISERROR(N'raised %d', 16, 1, 7);
                IF @kind = 4 INSERT INTO dbo.item (name, price) VALUES (N'apple', 1);
                IF @kind = 5 INSERT INTO dbo.item (name, price) VALUES (N'pear', -1);
                IF @kind = 6 SET @x = CAST(N'abc' AS int);
            END TRY
            BEGIN CATCH
                SET @number = ERROR_NUMBER();
                SET @message = CASE WHEN @kind IN (2, 3) THEN ERROR_MESSAGE() ELSE N'system' END;
                SET @proc = ERROR_PROCEDURE();
            END CATCH;
            SELECT @number AS number, @message AS message, @proc AS proc_name;
        END;
        GO
        CREATE PROCEDURE dbo.nested AS
        BEGIN
            DECLARE @outer int, @inner int, @after int;
            BEGIN TRY
                THROW 50001, N'outer', 1;
            END TRY
            BEGIN CATCH
                SET @outer = ERROR_NUMBER();
                BEGIN TRY
                    THROW 50002, N'inner', 1;
                END TRY
                BEGIN CATCH
                    SET @inner = ERROR_NUMBER();
                END CATCH;
                SET @after = ERROR_NUMBER();
            END CATCH;
            SELECT @outer AS outer_number, @inner AS inner_number, @after AS after_number;
        END;
        GO
        CREATE PROCEDURE dbo.add_item @name nvarchar(50), @price decimal(10, 2) AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            BEGIN TRY
                BEGIN TRAN;
                INSERT INTO dbo.item (name, price) VALUES (@name, @price);
                INSERT INTO dbo.error_log (number, message) VALUES (0, N'added ' + @name);
                COMMIT;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK TRAN;
                THROW;
            END CATCH;
        END;
        GO
        CREATE PROCEDURE dbo.reprice @name nvarchar(50), @price decimal(10, 2) AS
        BEGIN
            BEGIN TRAN;
            BEGIN TRY
                UPDATE dbo.item SET price = @price WHERE name = @name;
                IF @price > 100
                    THROW 50010, N'price too high', 1;
                COMMIT TRAN;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0
                    ROLLBACK TRAN;
                INSERT INTO dbo.error_log (number, message, procedure_name) VALUES (ERROR_NUMBER(), ERROR_MESSAGE(), ERROR_PROCEDURE());
                RETURN -1;
            END CATCH;
            RETURN 0;
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
        _source = await SqlServerDatabase.CreateAsync(Script, "trycatch");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("trycatch");
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

    /// <summary>ERROR_NUMBER() for errors each engine raises (division by zero, unique, check, conversion) and for THROW and RAISERROR.</summary>
    [TestCase("EXEC dbo.error_info 0")]
    [TestCase("EXEC dbo.error_info 1")]
    [TestCase("EXEC dbo.error_info 2")]
    [TestCase("EXEC dbo.error_info 3")]
    [TestCase("EXEC dbo.error_info 4")]
    [TestCase("EXEC dbo.error_info 5")]
    [TestCase("EXEC dbo.error_info 6")]
    [TestCase("EXEC dbo.nested")]
    public Task Same(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        return BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString);
    }

    /// <summary>
    /// The transaction patterns, called outside a transaction as an application would: a failing
    /// add_item (duplicate name) and reprice (price too high) leave no change but the error log,
    /// successful ones commit; both engines end with the same rows and status codes.
    /// </summary>
    [Test]
    public async Task TransactionsEndAsInSqlServer()
    {
        var sqlServerCodes = new List<string>();
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (string call in new[] { "EXEC dbo.add_item N'kiwi', 2", "EXEC dbo.add_item N'apple', 9" })
            {
                try
                {
                    await using var cmd = new SqlCommand(call, conn);
                    await cmd.ExecuteNonQueryAsync();
                    sqlServerCodes.Add("ok");
                }
                catch (SqlException ex)
                {
                    sqlServerCodes.Add($"error {ex.Number}");
                }
            }
            foreach (string call in new[] { "DECLARE @rc int; EXEC @rc = dbo.reprice N'bread', 4; SELECT @rc", "DECLARE @rc int; EXEC @rc = dbo.reprice N'bread', 500; SELECT @rc" })
            {
                await using var cmd = new SqlCommand(call, conn);
                sqlServerCodes.Add($"rc {await cmd.ExecuteScalarAsync()}");
            }
        }

        var postgresCodes = new List<string>();
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (string call in new[] { "CALL public.add_item('kiwi', 2)", "CALL public.add_item('apple', 9)" })
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(call, conn);
                    await cmd.ExecuteNonQueryAsync();
                    postgresCodes.Add("ok");
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    postgresCodes.Add("error 2627");
                }
            }
            foreach (string call in new[] { "CALL public.reprice('bread', 4)", "CALL public.reprice('bread', 500)" })
            {
                await using var cmd = new NpgsqlCommand(call, conn);
                postgresCodes.Add($"rc {await cmd.ExecuteScalarAsync()}");
            }
        }

        Assert.That(postgresCodes, Is.EqualTo(sqlServerCodes));
        await BothEngines.AssertSameResultAsync(
            "SELECT name, price FROM dbo.item ORDER BY name",
            "SELECT name, price FROM public.item ORDER BY name",
            null, null, _source.ConnectionString, _target.ConnectionString);
        await BothEngines.AssertSameResultAsync(
            "SELECT number, CASE WHEN number = 0 THEN message END AS message, procedure_name FROM dbo.error_log ORDER BY id",
            "SELECT number, CASE WHEN number = 0 THEN message END AS message, procedure_name FROM public.error_log ORDER BY id",
            null, null, _source.ConnectionString, _target.ConnectionString);
    }
}
