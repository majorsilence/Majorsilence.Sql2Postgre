using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Triggers converted by sql2pg and fired on both engines by the same multi-row statements:
/// AFTER triggers over inserted/deleted (per event, with UPDATE() and @@ROWCOUNT), a trigger that
/// rolls back, and an INSTEAD OF trigger on a view. The tables they leave must agree.
/// </summary>
[Category("Containers")]
public class TriggerBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int IDENTITY NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10, 2) NOT NULL);
        GO
        CREATE TABLE dbo.price_history (id int IDENTITY NOT NULL PRIMARY KEY, item_id int NOT NULL, old_price decimal(10, 2) NULL, new_price decimal(10, 2) NULL, rows_in_statement int NOT NULL);
        GO
        CREATE VIEW dbo.cheap_item AS SELECT id, name, price FROM dbo.item WHERE price < 5;
        GO
        CREATE TRIGGER dbo.item_prices ON dbo.item AFTER INSERT, UPDATE, DELETE AS
        BEGIN
            DECLARE @rows int = @@ROWCOUNT;
            IF @rows = 0
                RETURN;
            SET NOCOUNT ON;
            IF UPDATE(price) OR NOT EXISTS (SELECT 1 FROM deleted) OR NOT EXISTS (SELECT 1 FROM inserted)
                INSERT INTO dbo.price_history (item_id, old_price, new_price, rows_in_statement)
                SELECT COALESCE(i.id, d.id), d.price, i.price, @rows
                FROM inserted AS i FULL JOIN deleted AS d ON d.id = i.id;
        END;
        GO
        CREATE TRIGGER dbo.item_no_negative ON dbo.item FOR INSERT, UPDATE AS
        BEGIN
            IF EXISTS (SELECT 1 FROM inserted WHERE price < 0)
            BEGIN
                ROLLBACK TRANSACTION;
                RETURN;
            END;
        END;
        GO
        CREATE TRIGGER dbo.cheap_item_insert ON dbo.cheap_item INSTEAD OF INSERT AS
            INSERT INTO dbo.item (name, price) SELECT UPPER(name), price FROM inserted WHERE price < 5;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "triggers");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _target = await PostgresDatabase.CreateAsync("triggers");
        var schema = SchemaConverter.Convert(db, Options);
        var triggers = schema.Objects.Where(o => o.Kind == ObjectKind.Trigger).ToList();
        Assert.That(triggers, Has.Count.EqualTo(3));
        Assert.That(triggers.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", triggers.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
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
        // (SQL Server, PostgreSQL) statement pairs, each run on its own in autocommit, as an application would.
        (string Tsql, string Pg)[] statements =
        [
            ("INSERT INTO dbo.item (name, price) VALUES (N'apple', 1.20), (N'bread', 3.50), (N'cheese', 12.00)",
             "INSERT INTO public.item (name, price) VALUES ('apple', 1.20), ('bread', 3.50), ('cheese', 12.00)"),
            ("UPDATE dbo.item SET price = price + 1 WHERE price < 10", "UPDATE public.item SET price = price + 1 WHERE price < 10"),
            ("UPDATE dbo.item SET name = name + N'!' WHERE id = 3", "UPDATE public.item SET name = name || '!' WHERE id = 3"),
            ("UPDATE dbo.item SET price = 99 WHERE id = 999", "UPDATE public.item SET price = 99 WHERE id = 999"),
            ("INSERT INTO dbo.item (name, price) VALUES (N'bad', -1)", "INSERT INTO public.item (name, price) VALUES ('bad', -1)"),
            ("UPDATE dbo.item SET price = -5 WHERE id = 1", "UPDATE public.item SET price = -5 WHERE id = 1"),
            ("INSERT INTO dbo.cheap_item (name, price) VALUES (N'kiwi', 0.50), (N'melon', 7.00)",
             "INSERT INTO public.cheap_item (name, price) VALUES ('kiwi', 0.50), ('melon', 7.00)"),
            ("DELETE FROM dbo.item WHERE name = N'bread'", "DELETE FROM public.item WHERE name = 'bread'"),
        ];
        var sqlServerOutcomes = new List<string>();
        var postgresOutcomes = new List<string>();
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var (tsql, _) in statements)
            {
                try
                {
                    await using var cmd = new SqlCommand(tsql, conn);
                    await cmd.ExecuteNonQueryAsync();
                    sqlServerOutcomes.Add("ok");
                }
                catch (SqlException)
                {
                    sqlServerOutcomes.Add("rolled back");
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
                    await using var cmd = new NpgsqlCommand(pg, conn);
                    await cmd.ExecuteNonQueryAsync();
                    postgresOutcomes.Add("ok");
                }
                catch (PostgresException)
                {
                    postgresOutcomes.Add("rolled back");
                }
            }
        }

        Assert.That(postgresOutcomes, Is.EqualTo(sqlServerOutcomes));
        await BothEngines.AssertSameResultAsync("SELECT id, name, price FROM dbo.item ORDER BY id", "SELECT id, name, price FROM public.item ORDER BY id",
            null, null, _source.ConnectionString, _target.ConnectionString);
        await BothEngines.AssertSameResultAsync(
            "SELECT item_id, old_price, new_price, rows_in_statement FROM dbo.price_history ORDER BY item_id, old_price, new_price",
            "SELECT item_id, old_price, new_price, rows_in_statement FROM public.price_history ORDER BY item_id, old_price NULLS FIRST, new_price NULLS FIRST",
            null, null, _source.ConnectionString, _target.ConnectionString);
    }
}
