using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// WP 4.23, AdventureWorks' patterns: INSTEAD OF triggers on tables (BEFORE … FOR EACH ROW in
/// PostgreSQL) that refuse a delete with a ROLLBACK inside TRY (dVendor), turn a delete into an
/// update, and rewrite the rows an insert adds; and a computed column that calls a user-defined
/// function (Sales.Customer.AccountNumber), created before its table. The same statements leave
/// the same rows, and fail or succeed alike.
/// </summary>
[Category("Containers")]
public class InsteadOfTableBothEnginesTests
{
    private const string Script = """
        CREATE FUNCTION dbo.ufnLeadingZeros(@Value int) RETURNS varchar(8) WITH SCHEMABINDING AS
        BEGIN
            DECLARE @ReturnValue varchar(8);
            SET @ReturnValue = CONVERT(varchar(8), @Value);
            SET @ReturnValue = REPLICATE('0', 8 - DATALENGTH(@ReturnValue)) + @ReturnValue;
            RETURN (@ReturnValue);
        END;
        GO
        CREATE TABLE dbo.Vendor (VendorID int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL,
            AccountNumber AS (ISNULL('AW' + dbo.ufnLeadingZeros(VendorID), '')));
        GO
        CREATE TABLE dbo.Employee (EmployeeID int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, CurrentFlag bit NOT NULL DEFAULT 1);
        GO
        CREATE TABLE dbo.Tag (TagID int NOT NULL PRIMARY KEY, Label nvarchar(50) NOT NULL);
        GO
        INSERT INTO dbo.Vendor (VendorID, Name) VALUES (1, N'Acme'), (42, N'Globex');
        INSERT INTO dbo.Employee (EmployeeID, Name) VALUES (1, N'Ann'), (2, N'Bob'), (3, N'Cy');
        GO
        CREATE TRIGGER dbo.dVendor ON dbo.Vendor INSTEAD OF DELETE NOT FOR REPLICATION AS
        BEGIN
            DECLARE @Count int;
            SET @Count = @@ROWCOUNT;
            IF @Count = 0
                RETURN;
            SET NOCOUNT ON;
            BEGIN TRY
                DECLARE @DeleteCount int;
                SELECT @DeleteCount = COUNT(*) FROM deleted;
                IF @DeleteCount > 0
                BEGIN
                    RAISERROR (N'Vendors cannot be deleted. They can only be marked as not active.', 10, 1);
                    IF @@TRANCOUNT > 0
                    BEGIN
                        ROLLBACK TRANSACTION;
                    END
                END;
            END TRY
            BEGIN CATCH
                PRINT N'not reached';
                IF @@TRANCOUNT > 0
                BEGIN
                    ROLLBACK TRANSACTION;
                END
            END CATCH;
        END;
        GO
        CREATE TRIGGER dbo.dEmployee ON dbo.Employee INSTEAD OF DELETE AS
            UPDATE dbo.Employee SET CurrentFlag = 0 WHERE EmployeeID IN (SELECT EmployeeID FROM deleted);
        GO
        CREATE TRIGGER dbo.iTag ON dbo.Tag INSTEAD OF INSERT AS
            INSERT INTO dbo.Tag (TagID, Label) SELECT TagID + 100, UPPER(Label) FROM inserted;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "insteadof");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _target = await PostgresDatabase.CreateAsync("insteadof");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Where(o => o.Status == ObjectStatus.Manual).Select(o => $"{o.Source}: {string.Join("; ", o.Diagnostics.Select(d => d.Message))}"), Is.Empty);
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

    [Test]
    public async Task SameRowsAfterTheSameStatements()
    {
        (string Tsql, string Pg)[] statements =
        [
            ("DELETE FROM dbo.Vendor WHERE VendorID = 1", "DELETE FROM public.vendor WHERE vendorid = 1"),
            ("DELETE FROM dbo.Vendor WHERE VendorID = 999", "DELETE FROM public.vendor WHERE vendorid = 999"),
            ("INSERT INTO dbo.Vendor (VendorID, Name) VALUES (7, N'Initech')", "INSERT INTO public.vendor (vendorid, name) VALUES (7, 'Initech')"),
            ("DELETE FROM dbo.Employee WHERE EmployeeID IN (1, 3)", "DELETE FROM public.employee WHERE employeeid IN (1, 3)"),
            ("INSERT INTO dbo.Tag (TagID, Label) VALUES (1, N'red'), (2, N'blue')", "INSERT INTO public.tag (tagid, label) VALUES (1, 'red'), (2, 'blue')"),
        ];
        var sqlServer = new List<string>();
        var postgres = new List<string>();
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var (tsql, _) in statements)
            {
                try
                {
                    await new SqlCommand(tsql, conn).ExecuteNonQueryAsync();
                    sqlServer.Add("ok");
                }
                catch (SqlException)
                {
                    sqlServer.Add("failed");
                }
            }
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var (_, pg) in statements)
            {
                try
                {
                    await new NpgsqlCommand(pg, conn).ExecuteNonQueryAsync();
                    postgres.Add("ok");
                }
                catch (PostgresException)
                {
                    postgres.Add("failed");
                }
            }
        }

        Assert.That(postgres, Is.EqualTo(sqlServer));
        Assert.That(sqlServer, Is.EqualTo(new[] { "failed", "ok", "ok", "ok", "ok" }));
        await Same("SELECT VendorID, Name, AccountNumber FROM dbo.Vendor ORDER BY VendorID", "SELECT vendorid, name, accountnumber FROM public.vendor ORDER BY vendorid");
        await Same("SELECT EmployeeID, Name, CurrentFlag FROM dbo.Employee ORDER BY EmployeeID", "SELECT employeeid, name, currentflag FROM public.employee ORDER BY employeeid");
        await Same("SELECT TagID, Label FROM dbo.Tag ORDER BY TagID", "SELECT tagid, label FROM public.tag ORDER BY tagid");
    }

    private Task Same(string tsql, string pgsql) =>
        BothEngines.AssertSameResultAsync(tsql, pgsql, null, null, _source.ConnectionString, _target.ConnectionString);
}
