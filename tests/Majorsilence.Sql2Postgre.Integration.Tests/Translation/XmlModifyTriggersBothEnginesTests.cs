using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// WP 4.24, AdventureWorks' two triggers with xml modify(): Person.iuPerson inserts
/// TotalPurchaseYTD into a survey that lacks it, and Sales.iduSalesOrderDetail adds each order
/// line's total to it and takes away the old one (typed xml, decimal arithmetic on sql:column).
/// The same statements leave the same totals (read with value(): typed xml in SQL Server stores
/// 0.00 as 0, PostgreSQL keeps the text).
/// </summary>
[Category("Containers")]
public class XmlModifyTriggersBothEnginesTests
{
    private const string Survey = "http://schemas.microsoft.com/sqlserver/2004/07/adventure-works/IndividualSurvey";

    private const string Script = $$"""
        CREATE XML SCHEMA COLLECTION dbo.IndividualSurveySchemaCollection AS N'<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:t="{{Survey}}" targetNamespace="{{Survey}}" elementFormDefault="qualified"><xsd:element name="IndividualSurvey"><xsd:complexType><xsd:complexContent><xsd:restriction base="xsd:anyType"><xsd:sequence><xsd:element name="TotalPurchaseYTD" type="xsd:decimal" minOccurs="0"/><xsd:element name="Gender" type="xsd:string" minOccurs="0"/></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element></xsd:schema>';
        GO
        CREATE TABLE dbo.Person (BusinessEntityID int NOT NULL PRIMARY KEY, Demographics xml(dbo.IndividualSurveySchemaCollection) NULL);
        GO
        CREATE TABLE dbo.Customer (CustomerID int NOT NULL PRIMARY KEY, PersonID int NULL);
        GO
        CREATE TABLE dbo.SalesOrderHeader (SalesOrderID int NOT NULL PRIMARY KEY, CustomerID int NOT NULL, SubTotal money NOT NULL DEFAULT 0);
        GO
        CREATE TABLE dbo.SalesOrderDetail (SalesOrderID int NOT NULL, SalesOrderDetailID int NOT NULL PRIMARY KEY,
            OrderQty smallint NOT NULL, UnitPrice money NOT NULL,
            LineTotal AS (ISNULL(UnitPrice * OrderQty, 0.0)));
        GO
        CREATE TRIGGER dbo.iuPerson ON dbo.Person
        AFTER INSERT, UPDATE NOT FOR REPLICATION AS
        BEGIN
            DECLARE @Count int;
            SET @Count = @@ROWCOUNT;
            IF @Count = 0
                RETURN;
            SET NOCOUNT ON;
            IF UPDATE([BusinessEntityID]) OR UPDATE([Demographics])
            BEGIN
                UPDATE [dbo].[Person]
                SET [dbo].[Person].[Demographics] = N'<IndividualSurvey xmlns="{{Survey}}">
                    <TotalPurchaseYTD>0.00</TotalPurchaseYTD>
                    </IndividualSurvey>'
                FROM inserted
                WHERE [dbo].[Person].[BusinessEntityID] = inserted.[BusinessEntityID]
                    AND inserted.[Demographics] IS NULL;

                UPDATE [dbo].[Person]
                SET [Demographics].modify(N'declare default element namespace "{{Survey}}";
                    insert <TotalPurchaseYTD>0.00</TotalPurchaseYTD>
                    as first
                    into (/IndividualSurvey)[1]')
                FROM inserted
                WHERE [dbo].[Person].[BusinessEntityID] = inserted.[BusinessEntityID]
                    AND inserted.[Demographics] IS NOT NULL
                    AND inserted.[Demographics].exist(N'declare default element namespace
                        "{{Survey}}";
                        /IndividualSurvey/TotalPurchaseYTD') <> 1;
            END;
        END;
        GO
        CREATE TRIGGER dbo.iduSalesOrderDetail ON dbo.SalesOrderDetail
        AFTER INSERT, DELETE, UPDATE AS
        BEGIN
            DECLARE @Count int;
            SET @Count = @@ROWCOUNT;
            IF @Count = 0
                RETURN;
            SET NOCOUNT ON;
            BEGIN TRY
                IF UPDATE([OrderQty]) OR UPDATE([UnitPrice])
                BEGIN
                    UPDATE [dbo].[Person]
                    SET [Demographics].modify('declare default element namespace
                        "{{Survey}}";
                        replace value of (/IndividualSurvey/TotalPurchaseYTD)[1]
                        with data(/IndividualSurvey/TotalPurchaseYTD)[1] + sql:column ("inserted.LineTotal")')
                    FROM inserted
                        INNER JOIN [dbo].[SalesOrderHeader] AS SOH
                        ON inserted.[SalesOrderID] = SOH.[SalesOrderID]
                        INNER JOIN [dbo].[Customer] AS C
                        ON SOH.[CustomerID] = C.[CustomerID]
                    WHERE C.[PersonID] = [dbo].[Person].[BusinessEntityID];
                END;

                UPDATE [dbo].[SalesOrderHeader]
                SET [dbo].[SalesOrderHeader].[SubTotal] =
                    (SELECT SUM([dbo].[SalesOrderDetail].[LineTotal])
                        FROM [dbo].[SalesOrderDetail]
                        WHERE [dbo].[SalesOrderHeader].[SalesOrderID] = [dbo].[SalesOrderDetail].[SalesOrderID])
                WHERE [dbo].[SalesOrderHeader].[SalesOrderID] IN (SELECT inserted.[SalesOrderID] FROM inserted);

                UPDATE [dbo].[Person]
                SET [Demographics].modify('declare default element namespace
                    "{{Survey}}";
                    replace value of (/IndividualSurvey/TotalPurchaseYTD)[1]
                    with data(/IndividualSurvey/TotalPurchaseYTD)[1] - sql:column("deleted.LineTotal")')
                FROM deleted
                    INNER JOIN [dbo].[SalesOrderHeader]
                    ON deleted.[SalesOrderID] = [dbo].[SalesOrderHeader].[SalesOrderID]
                    INNER JOIN [dbo].[Customer]
                    ON [dbo].[Customer].[CustomerID] = [dbo].[SalesOrderHeader].[CustomerID]
                WHERE [dbo].[Customer].[PersonID] = [dbo].[Person].[BusinessEntityID];
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0
                BEGIN
                    ROLLBACK TRANSACTION;
                END
            END CATCH;
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "xmltriggers");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _target = await PostgresDatabase.CreateAsync("xmltriggers");
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
    public async Task SameTotalsAfterTheSameStatements()
    {
        string withTotal = $"<IndividualSurvey xmlns=\"{Survey}\"><TotalPurchaseYTD>100.5</TotalPurchaseYTD><Gender>F</Gender></IndividualSurvey>";
        string withoutTotal = $"<IndividualSurvey xmlns=\"{Survey}\"><Gender>M</Gender></IndividualSurvey>";
        (string Tsql, string Pg)[] statements =
        [
            ($"INSERT INTO dbo.Person VALUES (1, NULL), (2, N'{withTotal}'), (3, N'{withoutTotal}')",
                $"INSERT INTO public.person VALUES (1, NULL), (2, '{withTotal}'), (3, '{withoutTotal}')"),
            ("INSERT INTO dbo.Customer VALUES (1, 1), (2, 2), (3, 3)", "INSERT INTO public.customer VALUES (1, 1), (2, 2), (3, 3)"),
            ("INSERT INTO dbo.SalesOrderHeader (SalesOrderID, CustomerID) VALUES (10, 1), (11, 2), (12, 3)",
                "INSERT INTO public.salesorderheader (salesorderid, customerid) VALUES (10, 1), (11, 2), (12, 3)"),
            ("INSERT INTO dbo.SalesOrderDetail (SalesOrderID, SalesOrderDetailID, OrderQty, UnitPrice) VALUES (10, 1, 2, 10.25), (11, 2, 3, 1.1111), (12, 3, 1, 5)",
                "INSERT INTO public.salesorderdetail (salesorderid, salesorderdetailid, orderqty, unitprice) VALUES (10, 1, 2, 10.25), (11, 2, 3, 1.1111), (12, 3, 1, 5)"),
            ("INSERT INTO dbo.SalesOrderDetail (SalesOrderID, SalesOrderDetailID, OrderQty, UnitPrice) VALUES (10, 4, 1, 0.01)",
                "INSERT INTO public.salesorderdetail (salesorderid, salesorderdetailid, orderqty, unitprice) VALUES (10, 4, 1, 0.01)"),
            ("UPDATE dbo.SalesOrderDetail SET OrderQty = 4 WHERE SalesOrderDetailID = 1", "UPDATE public.salesorderdetail SET orderqty = 4 WHERE salesorderdetailid = 1"),
            ("DELETE FROM dbo.SalesOrderDetail WHERE SalesOrderDetailID = 2", "DELETE FROM public.salesorderdetail WHERE salesorderdetailid = 2"),
            ("UPDATE dbo.Person SET Demographics = NULL WHERE BusinessEntityID = 3", "UPDATE public.person SET demographics = NULL WHERE businessentityid = 3"),
        ];
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var (tsql, _) in statements)
                await new SqlCommand(tsql, conn).ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var (_, pg) in statements)
                await new NpgsqlCommand(pg, conn).ExecuteNonQueryAsync();
        }

        await BothEngines.AssertSameResultAsync(
            $"SELECT BusinessEntityID, Demographics.value('declare default element namespace \"{Survey}\"; (/IndividualSurvey/TotalPurchaseYTD)[1]', 'decimal(19,6)') AS total, "
                + $"Demographics.value('declare default element namespace \"{Survey}\"; (/IndividualSurvey/Gender)[1]', 'nvarchar(10)') AS gender, "
                + $"Demographics.value('declare default element namespace \"{Survey}\"; count(/IndividualSurvey/*)', 'int') AS elements FROM dbo.Person ORDER BY BusinessEntityID",
            $"SELECT businessentityid, (xpath('/d:IndividualSurvey/d:TotalPurchaseYTD/text()', demographics, ARRAY[ARRAY['d', '{Survey}']]))[1]::text::numeric(19,6) AS total, "
                + $"(xpath('/d:IndividualSurvey/d:Gender/text()', demographics, ARRAY[ARRAY['d', '{Survey}']]))[1]::text AS gender, "
                + $"(xpath('count(/d:IndividualSurvey/*)', demographics, ARRAY[ARRAY['d', '{Survey}']]))[1]::text::int AS elements FROM public.person ORDER BY businessentityid",
            null, null, _source.ConnectionString, _target.ConnectionString);
        await BothEngines.AssertSameResultAsync(
            "SELECT SalesOrderID, SubTotal FROM dbo.SalesOrderHeader ORDER BY SalesOrderID",
            "SELECT salesorderid, subtotal FROM public.salesorderheader ORDER BY salesorderid",
            null, null, _source.ConnectionString, _target.ConnectionString);

        // The totals the statements give, so the comparison is not of two unchanged columns.
        await using var check = new SqlConnection(_source.ConnectionString);
        await check.OpenAsync();
        var totals = new List<string>();
        await using (var reader = await new SqlCommand(
            $"SELECT CONCAT(BusinessEntityID, ':', Demographics.value('declare default element namespace \"{Survey}\"; (/IndividualSurvey/TotalPurchaseYTD)[1]', 'nvarchar(40)')) FROM dbo.Person ORDER BY 1", check).ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                totals.Add(reader.GetString(0));
        }
        Assert.That(totals, Is.EqualTo(new[] { "1:41.01", "2:100.5", "3:0" }));
    }
}
