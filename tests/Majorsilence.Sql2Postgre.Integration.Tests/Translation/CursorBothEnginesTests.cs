using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Cursors converted by sql2pg and run on both engines: the OPEN/FETCH/WHILE loop as a FOR loop,
/// scrolling, @@FETCH_STATUS, WHERE CURRENT OF, cursor variables, nested cursors, and variables
/// keeping their last row when FETCH runs past the end.
/// </summary>
[Category("Containers")]
public class CursorBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.item (id int IDENTITY NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10, 2) NOT NULL, category int NOT NULL);
        GO
        INSERT INTO dbo.item (name, price, category) VALUES (N'apple', 1.20, 1), (N'bread', 3.50, 2), (N'butter', 4.75, 2), (N'cheese', 12.00, 3), (N'banana', 0.80, 1);
        GO
        CREATE PROCEDURE dbo.price_list AS
        BEGIN
            DECLARE @name nvarchar(50), @price decimal(10, 2), @list nvarchar(max) = N'';
            DECLARE items CURSOR LOCAL FAST_FORWARD FOR
                SELECT name, price FROM dbo.item ORDER BY name;
            OPEN items;
            FETCH NEXT FROM items INTO @name, @price;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @list = @list + @name + N'=' + CAST(@price AS nvarchar(20)) + N';';
                FETCH NEXT FROM items INTO @name, @price;
            END;
            CLOSE items;
            DEALLOCATE items;
            SELECT @list AS list, @name AS last_name;
        END;
        GO
        CREATE PROCEDURE dbo.none_found AS
        BEGIN
            DECLARE @name nvarchar(50) = N'unchanged';
            DECLARE c CURSOR LOCAL FOR SELECT name FROM dbo.item WHERE price > 1000;
            OPEN c;
            FETCH NEXT FROM c INTO @name;
            WHILE @@FETCH_STATUS = 0
                FETCH NEXT FROM c INTO @name;
            CLOSE c;
            DEALLOCATE c;
            SELECT @name AS name;
        END;
        GO
        CREATE PROCEDURE dbo.ends AS
        BEGIN
            DECLARE @id int = -1, @first int, @last int, @second_last int, @status int;
            DECLARE ids CURSOR SCROLL FOR SELECT id FROM dbo.item ORDER BY id;
            OPEN ids;
            FETCH FIRST FROM ids INTO @first;
            FETCH LAST FROM ids INTO @last;
            FETCH PRIOR FROM ids INTO @second_last;
            FETCH ABSOLUTE 100 FROM ids INTO @id;
            SET @status = @@FETCH_STATUS;
            CLOSE ids;
            DEALLOCATE ids;
            SELECT @first AS first_id, @last AS last_id, @second_last AS second_last, @id AS id, @status AS status;
        END;
        GO
        CREATE PROCEDURE dbo.discounted @percent int AS
        BEGIN
            DECLARE @price decimal(10, 2);
            DECLARE c CURSOR LOCAL FOR SELECT price FROM dbo.item WHERE price > 4;
            OPEN c;
            WHILE 1 = 1
            BEGIN
                FETCH NEXT FROM c INTO @price;
                IF @@FETCH_STATUS <> 0
                    BREAK;
                UPDATE dbo.item SET price = @price * (100 - @percent) / 100 WHERE CURRENT OF c;
            END;
            CLOSE c;
            DEALLOCATE c;
            SELECT name, price FROM dbo.item ORDER BY name;
        END;
        GO
        CREATE PROCEDURE dbo.per_category AS
        BEGIN
            DECLARE @category int, @name nvarchar(50), @out nvarchar(max) = N'';
            DECLARE @categories CURSOR;
            SET @categories = CURSOR LOCAL FOR SELECT DISTINCT category FROM dbo.item ORDER BY category;
            OPEN @categories;
            FETCH NEXT FROM @categories INTO @category;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @out = @out + CAST(@category AS nvarchar(10)) + N':';
                DECLARE names CURSOR LOCAL FOR SELECT name FROM dbo.item WHERE category = @category ORDER BY name;
                OPEN names;
                FETCH NEXT FROM names INTO @name;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    SET @out = @out + @name + N',';
                    FETCH NEXT FROM names INTO @name;
                END;
                CLOSE names;
                DEALLOCATE names;
                SET @out = @out + N';';
                FETCH NEXT FROM @categories INTO @category;
            END;
            CLOSE @categories;
            DEALLOCATE @categories;
            SELECT @out AS result;
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
        _source = await SqlServerDatabase.CreateAsync(Script, "cursors");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("cursors");
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

    [TestCase("EXEC dbo.price_list")]
    [TestCase("EXEC dbo.none_found")]
    [TestCase("EXEC dbo.ends")]
    [TestCase("EXEC dbo.discounted 10")]
    [TestCase("EXEC dbo.per_category")]
    public Task Same(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        return BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString);
    }
}
