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
/// ERROR_*() in a procedure called from a CATCH block, as AdventureWorks' uspLogError does: the
/// logging procedure sees the caller's error (also after the CATCH block rolled back), and nothing
/// once the block is done. The calls run in autocommit on both engines, since a CATCH block here
/// ends a transaction; the log rows are compared.
/// </summary>
[Category("Containers")]
public class ErrorContextBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.ErrorLog (ErrorLogID int IDENTITY NOT NULL PRIMARY KEY, ErrorNumber int NULL, ErrorSeverity int NULL, ErrorState int NULL,
            ErrorProcedure nvarchar(126) NULL, ErrorLine int NULL, ErrorMessage nvarchar(4000) NULL, Note nvarchar(20) NOT NULL);
        GO
        CREATE PROCEDURE dbo.LogError @note nvarchar(20) AS
        BEGIN
            IF ERROR_NUMBER() IS NULL
                RETURN;
            INSERT INTO dbo.ErrorLog (ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorMessage, Note)
            VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_MESSAGE(), @note);
        END;
        GO
        CREATE PROCEDURE dbo.Work @kind int AS
        BEGIN
            DECLARE @x int;
            BEGIN TRY
                IF @kind = 1 THROW 50001, N'custom failure', 1;
                IF @kind = 2 SET @x = 1 / 0;
                IF @kind = 3 RAISERROR(N'raised %d', 16, 1, 7);
                IF @kind = 4
                BEGIN
                    BEGIN TRAN;
                    INSERT INTO dbo.ErrorLog (Note) VALUES (N'undone');
                    THROW 50004, N'in a transaction', 1;
                END;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0
                    ROLLBACK;
                EXEC dbo.LogError @note = N'caught';
            END CATCH;
            EXEC dbo.LogError @note = N'after';
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
        _source = await SqlServerDatabase.CreateAsync(Script, "errorctx");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("errorctx");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
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

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(0)]
    public async Task TheLoggerSeesTheCallersError(int kind)
    {
        string calls = $"DELETE FROM dbo.ErrorLog;\nEXEC dbo.Work {kind};\nEXEC dbo.LogError N'outside';";
        await using (var sql = new SqlConnection(_source.ConnectionString))
        {
            await sql.OpenAsync();
            foreach (string statement in calls.Split('\n'))
            {
                await using var cmd = new SqlCommand(statement, sql);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        var ran = new System.Text.StringBuilder();
        await using (var pg = new NpgsqlConnection(_target.ConnectionString))
        {
            await pg.OpenAsync();
            foreach (string statement in calls.Split('\n'))
            {
                var translation = ScriptTranslator.Translate(statement, _model, Options);
                Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
                ran.Append(translation.Sql);
                await using var cmd = new NpgsqlCommand(translation.Sql, pg);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        // A system error's message is the engine's own text.
        const string log = "SELECT ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorLine, CASE WHEN ErrorNumber < 50000 THEN N'system' ELSE ErrorMessage END AS Message, Note FROM dbo.ErrorLog ORDER BY ErrorLogID";
        var translated = ScriptTranslator.Translate(log, _model, Options);
        var expected = await BothEngines.RunSqlServerAsync(log, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translated.Sql, connectionString: _target.ConnectionString);
        Assert.That(expected.Rows, kind == 0 ? Is.Empty : Is.Not.Empty);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), ran + "\n" + translated.Sql);
    }
}
