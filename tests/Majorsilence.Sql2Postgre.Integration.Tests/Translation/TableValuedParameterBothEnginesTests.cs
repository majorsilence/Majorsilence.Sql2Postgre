using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Table-valued parameters, converted by sql2pg: a procedure and an inline function taking table
/// types run on both engines with the same rows, called from T-SQL (table-type variables, which
/// become arrays of the composite type), and the converted procedure is called from Npgsql with
/// arrays of a mapped composite, as application code would.
/// </summary>
[Category("Containers")]
public class TableValuedParameterBothEnginesTests
{
    private const string Script = """
        CREATE TYPE dbo.OrderList AS TABLE (OrderReference int NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Comments nvarchar(100) NULL);
        GO
        CREATE TYPE dbo.OrderLineList AS TABLE (OrderReference int NOT NULL, ItemId int NOT NULL, Quantity int NOT NULL);
        GO
        CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL);
        GO
        CREATE TABLE dbo.Orders (OrderId int IDENTITY(1, 1) NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Comments nvarchar(100) NULL, Reference int NOT NULL);
        GO
        CREATE TABLE dbo.OrderLines (OrderLineId int IDENTITY(1, 1) NOT NULL PRIMARY KEY, OrderId int NOT NULL, ItemId int NOT NULL, Quantity int NOT NULL);
        GO
        CREATE PROCEDURE dbo.InsertOrders @Orders dbo.OrderList READONLY, @Lines dbo.OrderLineList READONLY, @MinQuantity int = 1 AS
        BEGIN
            SET NOCOUNT ON;
            IF NOT EXISTS (SELECT 1 FROM @Orders)
                RETURN 0;
            INSERT INTO dbo.Orders (CustomerId, Comments, Reference)
            SELECT o.CustomerId, o.Comments, o.OrderReference FROM @Orders AS o ORDER BY o.OrderReference;
            INSERT INTO dbo.OrderLines (OrderId, ItemId, Quantity)
            SELECT d.OrderId, l.ItemId, l.Quantity
            FROM @Lines AS l JOIN dbo.Orders AS d ON d.Reference = l.OrderReference
            WHERE l.Quantity >= @MinQuantity AND d.OrderId > (SELECT COALESCE(MAX(OrderId), 0) FROM dbo.OrderLines);
            RETURN 1;
        END;
        GO
        CREATE FUNCTION dbo.OrdersFor(@Ids dbo.IdList READONLY) RETURNS TABLE AS
        RETURN SELECT o.OrderId, o.CustomerId, o.Comments FROM dbo.Orders AS o JOIN @Ids AS i ON i.Id = o.CustomerId;
        GO
        CREATE PROCEDURE dbo.PlaceOrder @Customer int, @Reference int AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @o dbo.OrderList, @l dbo.OrderLineList;
            INSERT INTO @o (OrderReference, CustomerId, Comments) VALUES (@Reference, @Customer, N'placed');
            INSERT INTO @l (OrderReference, ItemId, Quantity) VALUES (@Reference, 10, 2), (@Reference, 11, 0), (@Reference, 12, 5);
            EXEC dbo.InsertOrders @Orders = @o, @Lines = @l;
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
        _source = await SqlServerDatabase.CreateAsync(Script, "tvp");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("tvp");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
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

    /// <summary>A row of the OrderList composite, as application code would map it.</summary>
    public sealed class OrderRow
    {
        [PgName("orderreference")] public int OrderReference { get; set; }
        [PgName("customerid")] public int CustomerId { get; set; }
        [PgName("comments")] public string? Comments { get; set; }
    }

    public sealed class OrderLineRow
    {
        [PgName("orderreference")] public int OrderReference { get; set; }
        [PgName("itemid")] public int ItemId { get; set; }
        [PgName("quantity")] public int Quantity { get; set; }
    }

    [Test]
    public async Task SameRowsFromSqlAndFromNpgsql()
    {
        // From T-SQL: a procedure that fills table-type variables and passes them on.
        await RunBothAsync("EXEC dbo.PlaceOrder @Customer = 7, @Reference = 100");
        await RunBothAsync("EXEC dbo.PlaceOrder @Customer = 8, @Reference = 200");

        // From application code: SqlClient with DataTables, Npgsql with arrays of a mapped composite.
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            var orders = new System.Data.DataTable();
            orders.Columns.Add("OrderReference", typeof(int));
            orders.Columns.Add("CustomerId", typeof(int));
            orders.Columns.Add("Comments", typeof(string));
            orders.Rows.Add(300, 9, "from code");
            orders.Rows.Add(301, 7, null);
            var lines = new System.Data.DataTable();
            lines.Columns.Add("OrderReference", typeof(int));
            lines.Columns.Add("ItemId", typeof(int));
            lines.Columns.Add("Quantity", typeof(int));
            lines.Rows.Add(300, 20, 1);
            lines.Rows.Add(301, 21, 3);
            await using var cmd = new SqlCommand("dbo.InsertOrders", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.Add(new SqlParameter("@Orders", System.Data.SqlDbType.Structured) { TypeName = "dbo.OrderList", Value = orders });
            cmd.Parameters.Add(new SqlParameter("@Lines", System.Data.SqlDbType.Structured) { TypeName = "dbo.OrderLineList", Value = lines });
            await cmd.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlDataSourceBuilder(_target.ConnectionString);
        builder.MapComposite<OrderRow>("public.orderlist");
        builder.MapComposite<OrderLineRow>("public.orderlinelist");
        await using (var dataSource = builder.Build())
        await using (var cmd = dataSource.CreateCommand("CALL public.insertorders(orders => $1, lines => $2)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = new[] { new OrderRow { OrderReference = 300, CustomerId = 9, Comments = "from code" }, new OrderRow { OrderReference = 301, CustomerId = 7 } } });
            cmd.Parameters.Add(new NpgsqlParameter { Value = new[] { new OrderLineRow { OrderReference = 300, ItemId = 20, Quantity = 1 }, new OrderLineRow { OrderReference = 301, ItemId = 21, Quantity = 3 } } });
            await cmd.ExecuteNonQueryAsync();
        }

        await AssertSameAsync("SELECT OrderId, CustomerId, Comments, Reference FROM dbo.Orders ORDER BY OrderId");
        await AssertSameAsync("SELECT OrderId, ItemId, Quantity FROM dbo.OrderLines ORDER BY OrderId, ItemId");
        // The inline function from SQL: a table-type variable in T-SQL, ARRAY[ROW(…)::type] in PostgreSQL.
        var expected = await BothEngines.RunSqlServerAsync("""
            DECLARE @ids dbo.IdList;
            INSERT INTO @ids (Id) VALUES (7), (9);
            SELECT OrderId, CustomerId, Comments FROM dbo.OrdersFor(@ids) ORDER BY OrderId;
            """, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(
            "SELECT orderid, customerid, comments FROM public.ordersfor(ARRAY[ROW(7)::public.idlist, ROW(9)::public.idlist]) ORDER BY orderid",
            connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows));
        Assert.That(actual.Rows, Has.Count.EqualTo(3));
    }

    private async Task RunBothAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await new SqlCommand(tsql, conn).ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand(translation.Sql, conn).ExecuteNonQueryAsync();
        }
    }

    private async Task AssertSameAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }
}
