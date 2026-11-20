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
/// Procedures that change indexes, constraints and columns around a load, converted by sql2pg and
/// run on both engines: afterwards the rows, the indexes, the foreign key (enforced again, but not
/// trusted for the rows loaded while it was off) and the columns agree. Each procedure runs twice,
/// so DDL that only works once would fail.
/// </summary>
[Category("Containers")]
public class RoutineDdlBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Customer (CustomerId int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY, Name nvarchar(50) NOT NULL);
        GO
        CREATE TABLE dbo.Sale (SaleId int IDENTITY(1, 1) NOT NULL, CustomerId int NOT NULL, Amount decimal(10, 2) NOT NULL,
            Region nvarchar(10) NULL CONSTRAINT DF_Sale_Region DEFAULT N'north',
            CONSTRAINT PK_Sale PRIMARY KEY (SaleId),
            CONSTRAINT FK_Sale_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId),
            CONSTRAINT CK_Sale_Amount CHECK (Amount >= 0));
        GO
        CREATE INDEX IX_Sale_Customer ON dbo.Sale (CustomerId) INCLUDE (Amount);
        GO
        CREATE INDEX IX_Sale_Region ON dbo.Sale (Region) WHERE Region IS NOT NULL;
        GO
        INSERT INTO dbo.Customer (CustomerId, Name) VALUES (1, N'Ann'), (2, N'Bob');
        GO
        INSERT INTO dbo.Sale (CustomerId, Amount) VALUES (1, 10.00), (2, 20.00);
        GO
        CREATE PROCEDURE dbo.Scratch AS SELECT 1 AS one;
        GO
        CREATE PROCEDURE dbo.LoadSales @orphan int AS
        BEGIN
            SET NOCOUNT ON;
            ALTER TABLE dbo.Sale NOCHECK CONSTRAINT FK_Sale_Customer;
            ALTER TABLE dbo.Sale NOCHECK CONSTRAINT CK_Sale_Amount;
            DROP INDEX IF EXISTS IX_Sale_Customer ON dbo.Sale;
            ALTER INDEX IX_Sale_Region ON dbo.Sale DISABLE;
            INSERT INTO dbo.Sale (CustomerId, Amount, Region) VALUES (1, 5.00, N'east'), (@orphan, 7.00, N'west'), (2, -1.00, NULL);
            CREATE NONCLUSTERED INDEX IX_Sale_Customer ON dbo.Sale (CustomerId) INCLUDE (Amount) WITH (SORT_IN_TEMPDB = ON);
            ALTER INDEX IX_Sale_Region ON dbo.Sale REBUILD;
            ALTER INDEX ALL ON dbo.Sale REORGANIZE;
            ALTER TABLE dbo.Sale CHECK CONSTRAINT FK_Sale_Customer;
            ALTER TABLE dbo.Sale CHECK CONSTRAINT CK_Sale_Amount;
        END;
        GO
        CREATE PROCEDURE dbo.RebuildKeys AS
        BEGIN
            SET NOCOUNT ON;
            DELETE FROM dbo.Sale WHERE Amount < 0 OR CustomerId NOT IN (SELECT CustomerId FROM dbo.Customer);
            ALTER TABLE dbo.Sale DROP CONSTRAINT FK_Sale_Customer;
            ALTER TABLE dbo.Sale DROP CONSTRAINT PK_Sale;
            ALTER TABLE dbo.Sale ADD CONSTRAINT PK_Sale PRIMARY KEY NONCLUSTERED (SaleId);
            ALTER TABLE dbo.Sale WITH CHECK ADD CONSTRAINT FK_Sale_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId);
            ALTER TABLE dbo.Sale CHECK CONSTRAINT FK_Sale_Customer;
            ALTER TABLE dbo.Sale WITH CHECK CHECK CONSTRAINT CK_Sale_Amount;
        END;
        GO
        CREATE PROCEDURE dbo.Reshape AS
        BEGIN
            SET NOCOUNT ON;
            ALTER TABLE dbo.Sale ADD Note nvarchar(20) NULL, Rank int NOT NULL CONSTRAINT DF_Sale_Rank DEFAULT 3;
            -- Without WITH VALUES the existing rows' Flag stays NULL; with it they get the default.
            ALTER TABLE dbo.Sale ADD Flag int NULL CONSTRAINT DF_Sale_Flag DEFAULT 0, Kept int NULL CONSTRAINT DF_Sale_Kept DEFAULT 1 WITH VALUES;
            ALTER TABLE dbo.Sale DROP CONSTRAINT DF_Sale_Region;
            ALTER TABLE dbo.Sale ADD CONSTRAINT DF_Sale_Region DEFAULT N'south' FOR Region;
            ALTER TABLE dbo.Sale ALTER COLUMN Note nvarchar(40) NULL;
            DROP PROCEDURE IF EXISTS dbo.Scratch;
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
        _source = await SqlServerDatabase.CreateAsync(Script, "routineddl");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("routineddl");
        var schema = SchemaConverter.Convert(db, Options);
        var procedures = schema.Objects.Where(o => o.Kind == ObjectKind.Procedure).ToList();
        Assert.That(procedures.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", procedures.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
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

    [Test]
    public async Task SameStateAfterEachProcedure()
    {
        await LoadWithKeysAndIndexesOff();
        await RebuildKeysAndValidate();
        await ReshapeColumnsAndDropRoutine();
    }

    private async Task LoadWithKeysAndIndexesOff()
    {
        await RunBothAsync("EXEC dbo.LoadSales @orphan = 9");
        await RunBothAsync("EXEC dbo.LoadSales @orphan = 8");

        await AssertSameAsync(
            "SELECT CustomerId, Amount, Region FROM dbo.Sale ORDER BY SaleId",
            "SELECT customerid, amount, region FROM public.sale ORDER BY saleid");
        await AssertSameAsync(
            "SELECT LOWER(name) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Sale') AND name LIKE 'IX%' AND is_disabled = 0 ORDER BY name",
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'sale' AND indexname LIKE 'ix%' ORDER BY indexname");
        // Enforced again, but the rows loaded while they were off are not checked.
        await AssertSameAsync(
            "SELECT LOWER(name), is_not_trusted FROM sys.foreign_keys WHERE name = 'FK_Sale_Customer' UNION ALL SELECT LOWER(name), is_not_trusted FROM sys.check_constraints WHERE name = 'CK_Sale_Amount' ORDER BY 1",
            "SELECT conname, NOT convalidated FROM pg_constraint WHERE conname IN ('fk_sale_customer', 'ck_sale_amount') ORDER BY 1");
        await Assert.ThatAsync(() => SqlServerAsync("INSERT INTO dbo.Sale (CustomerId, Amount) VALUES (77, 1.00)"), Throws.InstanceOf<SqlException>());
        await Assert.ThatAsync(() => PostgresAsync("INSERT INTO public.sale (customerid, amount) VALUES (77, 1.00)"), Throws.InstanceOf<PostgresException>());
    }

    private async Task RebuildKeysAndValidate()
    {
        await RunBothAsync("EXEC dbo.RebuildKeys");
        await RunBothAsync("EXEC dbo.RebuildKeys");

        await AssertSameAsync(
            "SELECT CustomerId, Amount FROM dbo.Sale ORDER BY SaleId",
            "SELECT customerid, amount FROM public.sale ORDER BY saleid");
        await AssertSameAsync(
            "SELECT LOWER(name), is_not_trusted FROM sys.foreign_keys WHERE name = 'FK_Sale_Customer' UNION ALL SELECT LOWER(name), is_not_trusted FROM sys.check_constraints WHERE name = 'CK_Sale_Amount' UNION ALL SELECT LOWER(name), 0 FROM sys.key_constraints WHERE name = 'PK_Sale' ORDER BY 1",
            "SELECT conname, NOT convalidated FROM pg_constraint WHERE conname IN ('fk_sale_customer', 'ck_sale_amount', 'pk_sale') ORDER BY 1");
    }

    private async Task ReshapeColumnsAndDropRoutine()
    {
        await RunBothAsync("EXEC dbo.Reshape");
        await SqlServerAsync("INSERT INTO dbo.Sale (CustomerId, Amount, Note) VALUES (1, 2.00, N'later')");
        await PostgresAsync("INSERT INTO public.sale (customerid, amount, note) VALUES (1, 2.00, 'later')");

        await AssertSameAsync(
            "SELECT LOWER(COLUMN_NAME), IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Sale' ORDER BY ORDINAL_POSITION",
            "SELECT column_name, is_nullable, character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'sale' ORDER BY ordinal_position");
        await AssertSameAsync(
            "SELECT Region, Note, Rank FROM dbo.Sale WHERE Note IS NOT NULL",
            "SELECT region, note, rank FROM public.sale WHERE note IS NOT NULL");
        await AssertSameAsync(
            "SELECT Note, Flag, Kept FROM dbo.Sale ORDER BY SaleId",
            "SELECT note, flag, kept FROM public.sale ORDER BY saleid");
        await AssertSameAsync(
            "SELECT COUNT(*) FROM sys.procedures WHERE name = 'Scratch'",
            "SELECT COUNT(*) FROM pg_proc WHERE proname = 'scratch'");
    }

    /// <summary>Runs the T-SQL on SQL Server and its translation on PostgreSQL.</summary>
    private async Task RunBothAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await SqlServerAsync(tsql);
        await PostgresAsync(translation.Sql);
    }

    private async Task AssertSameAsync(string tsql, string pgsql)
    {
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(pgsql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), pgsql);
    }

    private async Task SqlServerAsync(string sql)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task PostgresAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
