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
/// DDL that procedures run as dynamic SQL, converted by sql2pg and run on both engines (WP 4.18):
/// a trigger whose CREATE text is put together from variables set to constants, dropped again by
/// a static DROP TRIGGER; a procedure created by dynamic SQL and called by another procedure; a
/// security policy with its predicate function, as row-level security; ALTER SEQUENCE with a
/// number put in at run time; SESSION_CONTEXT. Each procedure runs twice, so DDL that only works
/// once would fail. PostgreSQL reads the policy's table as an ordinary role: a superuser
/// bypasses row-level security.
/// </summary>
[Category("Containers")]
public class DynamicDdlBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Item (Id int NOT NULL CONSTRAINT PK_Item PRIMARY KEY, Name nvarchar(50) NOT NULL);
        GO
        CREATE TABLE dbo.ItemAudit (AuditId int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ItemAudit PRIMARY KEY, ItemId int NOT NULL, Note nvarchar(100) NOT NULL);
        GO
        CREATE TABLE dbo.Sale (SaleId int NOT NULL CONSTRAINT PK_Sale PRIMARY KEY, Region nvarchar(10) NOT NULL);
        GO
        INSERT INTO dbo.Sale (SaleId, Region) VALUES (1, N'north'), (2, N'south'), (3, N'north');
        GO
        CREATE SEQUENCE dbo.Ticket AS int START WITH 1;
        GO
        CREATE PROCEDURE dbo.AddAudit AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @SchemaName sysname = N'dbo';
            DECLARE @TableName sysname = N'Item';
            DECLARE @CrLf nvarchar(2) = NCHAR(13) + NCHAR(10);
            DECLARE @SQL nvarchar(max) = N'DROP TRIGGER IF EXISTS ' + QUOTENAME(@SchemaName) + N'.[TR_' + @TableName + N'_Audit];';
            EXECUTE (@SQL);
            SET @SQL = N'CREATE TRIGGER ' + QUOTENAME(@SchemaName) + N'.[TR_' + @TableName + N'_Audit]' + @CrLf
                     + N'ON ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName) + @CrLf
                     + N'AFTER INSERT' + @CrLf
                     + N'AS' + @CrLf
                     + N'BEGIN' + @CrLf
                     + N'    SET NOCOUNT ON;' + @CrLf
                     + N'    INSERT dbo.ItemAudit (ItemId, Note) SELECT i.Id, N''added '' + i.Name FROM inserted AS i;' + @CrLf
                     + N'END;';
            EXECUTE (@SQL);
        END;
        GO
        CREATE PROCEDURE dbo.RemoveAudit AS
        BEGIN
            DROP TRIGGER IF EXISTS dbo.TR_Item_Audit;
        END;
        GO
        CREATE PROCEDURE dbo.MakeDoubler AS
        BEGIN
            DECLARE @SQL nvarchar(max) = N'DROP PROCEDURE IF EXISTS dbo.Doubler;';
            EXECUTE (@SQL);
            SET @SQL = N'CREATE PROCEDURE dbo.Doubler @x int, @y int OUTPUT AS BEGIN SET @y = @x * 2; END;';
            EXECUTE (@SQL);
        END;
        GO
        CREATE PROCEDURE dbo.UseDoubler @x int AS
        BEGIN
            DECLARE @y int;
            EXEC dbo.Doubler @x = @x, @y = @y OUTPUT;
            INSERT INTO dbo.ItemAudit (ItemId, Note) VALUES (@x, N'doubled ' + CAST(@y AS nvarchar(10)));
        END;
        GO
        CREATE PROCEDURE dbo.ApplyPolicy AS
        BEGIN
            DECLARE @SQL nvarchar(max);
            SET @SQL = N'DROP SECURITY POLICY IF EXISTS dbo.SaleRegion;';
            EXECUTE (@SQL);
            SET @SQL = N'DROP FUNCTION IF EXISTS dbo.RegionVisible;';
            EXECUTE (@SQL);
            SET @SQL = N'CREATE FUNCTION dbo.RegionVisible(@Region nvarchar(10)) RETURNS TABLE WITH SCHEMABINDING AS RETURN (SELECT 1 AS Visible WHERE @Region = N''north'');';
            EXECUTE (@SQL);
            SET @SQL = N'CREATE SECURITY POLICY dbo.SaleRegion ADD FILTER PREDICATE dbo.RegionVisible(Region) ON dbo.Sale, ADD BLOCK PREDICATE dbo.RegionVisible(Region) ON dbo.Sale AFTER INSERT;';
            EXECUTE (@SQL);
        END;
        GO
        CREATE PROCEDURE dbo.RemovePolicy AS
        BEGIN
            DECLARE @SQL nvarchar(max);
            SET @SQL = N'DROP SECURITY POLICY IF EXISTS dbo.SaleRegion;';
            EXECUTE (@SQL);
            SET @SQL = N'DROP FUNCTION IF EXISTS dbo.RegionVisible;';
            EXECUTE (@SQL);
        END;
        GO
        CREATE PROCEDURE dbo.Reseed @SequenceName sysname, @Next bigint AS
        BEGIN
            DECLARE @SQL nvarchar(max) = N'ALTER SEQUENCE dbo.' + QUOTENAME(@SequenceName) + N' RESTART WITH ' + CAST(@Next AS nvarchar(20)) + N';';
            EXECUTE (@SQL);
        END;
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private static readonly string Reader = $"sql2pg_rls_{Guid.NewGuid():N}"[..24];
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;
    private string _readerConnection = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "dynamicddl");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("dynamicddl");
        var schema = SchemaConverter.Convert(db, Options);
        var procedures = schema.Objects.Where(o => o.Kind == ObjectKind.Procedure).ToList();
        Assert.That(procedures.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", procedures.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));

        // An ordinary role reads the table with the policy (row-level security does not apply to superusers).
        await PostgresAsync($"CREATE ROLE {Reader} LOGIN PASSWORD 'reader'; GRANT USAGE ON SCHEMA public TO {Reader}; GRANT SELECT, INSERT ON public.sale TO {Reader};");
        _readerConnection = new NpgsqlConnectionStringBuilder(_target.ConnectionString) { Username = Reader, Password = "reader", Pooling = false }.ConnectionString;
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
        {
            await _target.DisposeAsync();
            await using var conn = new NpgsqlConnection(await Engines.PostgresAsync());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP ROLE IF EXISTS {Reader}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Test]
    public async Task TriggerCreatedAndDroppedByProcedures()
    {
        await RunBothAsync("EXEC dbo.AddAudit");
        await RunBothAsync("EXEC dbo.AddAudit");
        await RunBothAsync("INSERT INTO dbo.Item (Id, Name) VALUES (1, N'apple')");
        await RunBothAsync("EXEC dbo.RemoveAudit");
        await RunBothAsync("EXEC dbo.RemoveAudit");
        await RunBothAsync("INSERT INTO dbo.Item (Id, Name) VALUES (2, N'pear')");
        await AssertSameAsync("SELECT ItemId, Note FROM dbo.ItemAudit ORDER BY AuditId", "SELECT itemid, note FROM public.itemaudit ORDER BY auditid");
        await AssertSameAsync(
            "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_Item_Audit'",
            "SELECT COUNT(*) FROM pg_trigger WHERE tgname = 'tr_item_audit'");
    }

    [Test]
    public async Task ProcedureCreatedByDynamicSqlIsCalled()
    {
        await RunBothAsync("EXEC dbo.MakeDoubler");
        await RunBothAsync("EXEC dbo.MakeDoubler");
        await RunBothAsync("EXEC dbo.UseDoubler @x = 21");
        await AssertSameAsync("SELECT ItemId, Note FROM dbo.ItemAudit WHERE Note LIKE N'doubled%'", "SELECT itemid, note FROM public.itemaudit WHERE note LIKE 'doubled%'");
    }

    [Test]
    public async Task SecurityPolicyFiltersAndBlocks()
    {
        await RunBothAsync("EXEC dbo.ApplyPolicy");
        await RunBothAsync("EXEC dbo.ApplyPolicy");
        await AssertSameAsync("SELECT SaleId, Region FROM dbo.Sale ORDER BY SaleId", "SELECT saleid, region FROM public.sale ORDER BY saleid", _readerConnection);
        // The block predicate refuses a row the filter would hide; a visible one goes in.
        await Assert.ThatAsync(() => SqlServerAsync("INSERT INTO dbo.Sale (SaleId, Region) VALUES (4, N'south')"), Throws.InstanceOf<SqlException>());
        await Assert.ThatAsync(() => PostgresAsync("INSERT INTO public.sale (saleid, region) VALUES (4, 'south')", _readerConnection), Throws.InstanceOf<PostgresException>());
        await SqlServerAsync("INSERT INTO dbo.Sale (SaleId, Region) VALUES (5, N'north')");
        await PostgresAsync("INSERT INTO public.sale (saleid, region) VALUES (5, 'north')", _readerConnection);

        await RunBothAsync("EXEC dbo.RemovePolicy");
        await RunBothAsync("EXEC dbo.RemovePolicy");
        await AssertSameAsync("SELECT SaleId, Region FROM dbo.Sale ORDER BY SaleId", "SELECT saleid, region FROM public.sale ORDER BY saleid", _readerConnection);
        await AssertSameAsync(
            "SELECT COUNT(*) FROM sys.objects WHERE name = 'RegionVisible'",
            "SELECT COUNT(*) FROM pg_proc WHERE proname = 'regionvisible'");
    }

    [Test]
    public async Task SequenceRestartedWithANumberPutInAtRunTime()
    {
        await RunBothAsync("EXEC dbo.Reseed @SequenceName = N'Ticket', @Next = 500");
        await AssertSameAsync("SELECT NEXT VALUE FOR dbo.Ticket", "SELECT nextval('public.ticket')");
    }

    [TestCase("SELECT CAST(SESSION_CONTEXT(N'who') AS nvarchar(20)) AS who", "EXEC sp_set_session_context N'who', N'alice';", "SET sql2pg.session_who = 'alice';")]
    [TestCase("SELECT CAST(SESSION_CONTEXT(N'nobody') AS nvarchar(20)) AS who", null, null)]
    public async Task SessionContext(string tsql, string? sqlServerSetup, string? postgresSetup)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        var expected = await BothEngines.RunSqlServerAsync(tsql, sqlServerSetup, _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, postgresSetup, _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }

    /// <summary>Runs the T-SQL on SQL Server and its translation on PostgreSQL.</summary>
    private async Task RunBothAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await SqlServerAsync(tsql);
        await PostgresAsync(translation.Sql);
    }

    private async Task AssertSameAsync(string tsql, string pgsql, string? postgresConnection = null)
    {
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(pgsql, connectionString: postgresConnection ?? _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), pgsql);
    }

    private async Task SqlServerAsync(string sql)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task PostgresAsync(string sql, string? connection = null)
    {
        await using var conn = new NpgsqlConnection(connection ?? _target.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
