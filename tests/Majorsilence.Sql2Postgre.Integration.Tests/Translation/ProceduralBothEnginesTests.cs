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
/// T-SQL batches with variables and control flow, run on SQL Server and, translated to a DO
/// block, on a PostgreSQL copy of the shop fixture migrated by sql2pg. Each prints what it
/// computes; the messages (PRINT/RAISERROR/THROW on one side, NOTICE/EXCEPTION on the other) must
/// be the same, in the same order. Both run in a transaction that is rolled back.
/// </summary>
[Category("Containers")]
public class ProceduralBothEnginesTests
{
    private const string Data = """
        INSERT INTO shop.customer (CustomerCode, name, email) VALUES ('C1', N'alice', N'a@x'), ('C2', N'bob', NULL), ('C3', N'carol', N'c@x'), ('C4', N'dave', N'd@x');
        INSERT INTO shop.product (product_id, name, parent_id) VALUES (1, N'root', NULL), (2, N'child a', 1), (3, N'child b', 1), (4, N'grandchild', 2), (5, N'unused', NULL);
        INSERT INTO shop.[order] (customer_id, order_date, total) VALUES (100, '2024-01-05', 0), (100, '2024-02-10', 0), (105, '2023-02-11', 0), (110, '2024-03-01', 0);
        INSERT INTO shop.order_line (order_id, line_no, product_id, qty, unit_price) VALUES (1, 1, 2, 2, 10.00), (1, 2, 3, 1, 5.50), (2, 1, 2, 5, 10.00), (3, 1, 4, 1, 99.99), (4, 1, NULL, 3, 1.25);
        GO
        DISABLE TRIGGER shop.trg_order_line_total ON shop.order_line;
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + Data, "procedural");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("procedural");
        var schema = SchemaConverter.Convert(db, Options);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        var copy = await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString);
        Assert.That(copy.Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero);
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    private async Task<List<string>> SqlServerMessagesAsync(string tsql)
    {
        var messages = new List<string>();
        await using var conn = new SqlConnection(_source.ConnectionString) { FireInfoMessageEventOnUserErrors = true };
        conn.InfoMessage += (_, e) =>
        {
            // SQL Server sends an empty message (PRINT '' or PRINT NULL) as a single space.
            foreach (SqlError error in e.Errors)
                messages.Add(error.Class > 10 ? "ERROR: " + error.Message : error.Message == " " ? "" : error.Message);
        };
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        await using (var cmd = new SqlCommand(tsql, conn, tx))
            await cmd.ExecuteNonQueryAsync();
        if (tx.Connection is not null)
            await tx.RollbackAsync();
        return messages;
    }

    private async Task<List<string>> PostgresMessagesAsync(string sql)
    {
        var messages = new List<string>();
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001" || ex.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            messages.Add("ERROR: " + ex.MessageText);
        }
        await tx.RollbackAsync();
        return messages;
    }

    private async Task SameMessages(string tsql, params string[] expected)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var sqlServer = await SqlServerMessagesAsync(tsql);
        Assert.That(sqlServer, Is.EqualTo(expected), "SQL Server printed something else than the test expects");
        List<string> postgres;
        try
        {
            postgres = await PostgresMessagesAsync(translation.Sql);
        }
        catch (PostgresException ex)
        {
            Assert.Fail($"{ex.MessageText}\n\n{translation.Sql}");
            return;
        }
        Assert.That(postgres, Is.EqualTo(sqlServer), translation.Sql);
    }

    [Test]
    public Task VariablesConvertOnAssignmentAsSqlServerDoes() => SameMessages("""
        DECLARE @d decimal(10, 2) = 12.75, @i int, @s varchar(3), @n nvarchar(20) = N'ab';
        SET @i = @d;
        PRINT @i;
        SET @i = -@d;
        PRINT @i;
        SET @s = 'abcdef';
        PRINT @s;
        SET @n += N'cd';
        PRINT @n;
        SET @i += 5;
        SET @i *= 2;
        PRINT @i;
        DECLARE @b bit = 1;
        PRINT @b;
        SET @b = 5;
        PRINT @b;
        DECLARE @f float = 2.5;
        SET @i = @f;
        PRINT @i;
        PRINT @d;
        DECLARE @when date = '2024-02-29';
        PRINT @when;
        PRINT NULL;
        PRINT '';
        PRINT 'done';
        """, "12", "-12", "abc", "abcd", "-14", "1", "1", "2", "12.75", "2024-02-29", "", "", "done");

    [Test]
    public Task LoopsAndBranches() => SameMessages("""
        DECLARE @i int = 0, @odd int = 0, @msg varchar(100) = '';
        WHILE @i < 20
        BEGIN
            SET @i += 1;
            IF @i % 2 = 0
                CONTINUE;
            SET @odd += 1;
            SET @msg = @msg + CAST(@i AS varchar(5)) + ',';
            IF @odd >= 5
                BREAK;
        END
        PRINT @msg;
        IF @odd = 4
            PRINT 'four';
        ELSE IF @odd = 5
        BEGIN
            PRINT 'five';
        END
        ELSE
            PRINT 'other';
        WHILE 1 = 1
        BEGIN
            SET @i -= 3;
            IF @i < 0
                BREAK;
        END
        PRINT @i;
        IF @i > 100
            RETURN;
        PRINT 'end';
        """, "1,3,5,7,9,", "five", "-3", "end");

    [Test]
    public Task SelectAssignsFromTheLastRowAndKeepsValuesWhenThereIsNone() => SameMessages("""
        DECLARE @c int, @q int, @name nvarchar(100) = N'unchanged', @e nvarchar(200), @list nvarchar(max) = N'', @x int = 1;
        SELECT @c = COUNT(*), @q = SUM(qty) FROM shop.order_line;
        PRINT @c;
        PRINT @q;
        SELECT @name = name FROM shop.product WHERE product_id = 99;
        PRINT @name;
        SELECT @name = name FROM shop.product WHERE product_id = 2;
        PRINT @name;
        SELECT @name = name FROM shop.product ORDER BY product_id;
        PRINT @name;
        SELECT @list = @list + name + N';' FROM shop.product WHERE parent_id IS NOT NULL ORDER BY product_id;
        PRINT @list;
        SELECT TOP (1) @name = name FROM shop.customer ORDER BY name DESC;
        PRINT @name;
        SELECT @x = 5 WHERE 1 = 0;
        PRINT @x;
        SELECT @e = email FROM shop.customer WHERE customer_id = 999;
        PRINT ISNULL(@e, N'null');
        SELECT @e = email FROM shop.customer WHERE customer_id = 110;
        PRINT @e;
        """, "5", "12", "unchanged", "child a", "unused", "child a;child b;grandchild;", "dave", "1", "null", "c@x");

    [Test]
    public Task RowCountAndScopeIdentity() => SameMessages("""
        DECLARE @id bigint, @rows int;
        UPDATE shop.customer SET is_active = 0 WHERE email IS NULL;
        PRINT @@ROWCOUNT;
        SET @rows = @@ROWCOUNT;
        PRINT @rows;
        INSERT INTO shop.[order] (customer_id, order_date, total) VALUES (100, '2024-05-01', 1);
        SET @id = SCOPE_IDENTITY();
        PRINT @id;
        INSERT INTO shop.[order] (customer_id, order_date, total)
        SELECT customer_id, '2024-05-02', 2 FROM shop.customer WHERE is_active = 1;
        SELECT @id = SCOPE_IDENTITY(), @rows = @@ROWCOUNT;
        PRINT @id;
        PRINT @rows;
        DELETE FROM shop.[order] WHERE order_id >= 5;
        IF @@ROWCOUNT = 4
            PRINT 'deleted four';
        """, "1", "0", "5", "8", "3", "deleted four");

    [Test]
    public Task MessagesAndErrors() => SameMessages("""
        DECLARE @n int = 7;
        RAISERROR('progress %d of %s', 10, 1, @n, N'ten') WITH NOWAIT;
        PRINT 'between';
        WAITFOR DELAY '00:00:00.1';
        RAISERROR('failed at %d', 16, 1, @n);
        RETURN;
        """, "progress 7 of ten", "between", "ERROR: failed at 7");

    [Test]
    public Task ThrowEndsTheBatch() => SameMessages("""
        PRINT 'before';
        THROW 50001, N'custom failure', 1;
        PRINT 'not reached';
        """, "before", "ERROR: custom failure");
}
