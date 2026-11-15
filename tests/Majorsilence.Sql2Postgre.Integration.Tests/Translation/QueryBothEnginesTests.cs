using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Queries and DML on the shop fixture with data, run as T-SQL on SQL Server and translated on a
/// PostgreSQL copy migrated by sql2pg itself. A DML statement runs inside the rolled-back
/// transaction before a SELECT of the table's state (or returns its OUTPUT / RETURNING rows).
/// The fixture's trigger is disabled on SQL Server, since triggers are not translated yet.
/// </summary>
[Category("Containers")]
public class QueryBothEnginesTests
{
    private const string Data = """
        INSERT INTO shop.customer (CustomerCode, name, email) VALUES ('C1', N'alice', N'a@x'), ('C2', N'bob', NULL), ('C3', N'carol', N'c@x'), ('C4', N'dave', N'd@x');
        INSERT INTO shop.product (product_id, name, parent_id) VALUES (1, N'root', NULL), (2, N'child a', 1), (3, N'child b', 1), (4, N'grandchild', 2), (5, N'unused', NULL);
        INSERT INTO shop.[order] (customer_id, order_date, total) VALUES (100, '2024-01-05', 0), (100, '2024-02-10', 0), (105, '2023-02-11', 0), (110, '2024-03-01', 0);
        INSERT INTO shop.order_line (order_id, line_no, product_id, qty, unit_price) VALUES (1, 1, 2, 2, 10.00), (1, 2, 3, 1, 5.50), (2, 1, 2, 5, 10.00), (3, 1, 4, 1, 99.99), (4, 1, NULL, 3, 1.25);
        INSERT INTO shop.sales_fact VALUES (1, 10.00), (2, 20.50);
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
        _source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + Data, "queries");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("queries");
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

    private string Translate(string tsql)
    {
        var script = (TSqlScript)new TSql160Parser(true).Parse(new StringReader(tsql), out var errors);
        Assert.That(errors, Is.Empty, $"'{tsql}' does not parse");
        var names = new NameMapper(Options);
        var queries = new QueryTranslator(new ExpressionTranslator(Options, names, new TypeMapper(Options, names), _model));
        var scope = new Scope();
        return string.Concat(script.Batches.SelectMany(b => b.Statements)
            .Select(s => PgWriter.Write(queries.Statement(s, new ExpressionContext(scope, "dbo", new ObjectName("dbo", "test"))), Options.TargetVersion)));
    }

    private Task Same(string tsql, bool json = false) =>
        BothEngines.AssertSameResultAsync(tsql, Translate(tsql), null, null, _source.ConnectionString, _target.ConnectionString, json);

    /// <summary>A DML statement, then the table's rows.</summary>
    private Task SameAfter(string dml, string select) =>
        BothEngines.AssertSameResultAsync(select, Translate(select), dml, Translate(dml), _source.ConnectionString, _target.ConnectionString);

    [TestCase("SELECT TOP 2 name FROM shop.customer ORDER BY name DESC")]
    [TestCase("SELECT TOP (1) WITH TIES customer_id, order_date FROM shop.[order] ORDER BY customer_id")]
    [TestCase("SELECT TOP 50 PERCENT order_id FROM shop.[order] ORDER BY order_id")]
    [TestCase("SELECT name FROM shop.customer ORDER BY name OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    [TestCase("SELECT email AS e, name FROM shop.customer ORDER BY e, name")]
    [TestCase("SELECT email FROM shop.customer ORDER BY email DESC")]
    [TestCase("SELECT DISTINCT customer_id FROM shop.[order]")]
    [TestCase("SELECT c.name, o.order_id, l.qty FROM shop.customer c LEFT JOIN shop.[order] o ON o.customer_id = c.customer_id LEFT JOIN shop.order_line l ON l.order_id = o.order_id")]
    [TestCase("SELECT customer.name, [order].order_id FROM shop.customer INNER JOIN shop.[order] ON [order].customer_id = customer.customer_id")]
    [TestCase("SELECT c.name, o.order_id FROM shop.customer c FULL JOIN shop.[order] o ON o.customer_id = c.customer_id AND o.order_id > 1")]
    [TestCase("SELECT c.name, x.order_date FROM shop.customer c CROSS APPLY (SELECT TOP 1 o.order_date FROM shop.[order] o WHERE o.customer_id = c.customer_id ORDER BY o.order_date DESC) x")]
    [TestCase("SELECT c.name, x.n FROM shop.customer c OUTER APPLY (SELECT COUNT(*) AS n FROM shop.[order] o WHERE o.customer_id = c.customer_id) x")]
    [TestCase("WITH recent AS (SELECT customer_id, MAX(order_date) AS last_order FROM shop.[order] GROUP BY customer_id) SELECT c.name, r.last_order FROM shop.customer c JOIN recent r ON r.customer_id = c.customer_id")]
    [TestCase("WITH tree (id, depth) AS (SELECT product_id, 0 FROM shop.product WHERE parent_id IS NULL UNION ALL SELECT p.product_id, t.depth + 1 FROM shop.product p JOIN tree t ON p.parent_id = t.id) SELECT id, depth FROM tree ORDER BY id")]
    [TestCase("SELECT customer_id, COUNT(*) AS n, SUM(total) AS total FROM shop.[order] GROUP BY customer_id HAVING COUNT(*) > 1")]
    [TestCase("SELECT customer_id, YEAR(order_date) AS y, COUNT(*) AS n FROM shop.[order] GROUP BY ROLLUP (customer_id, YEAR(order_date))")]
    [TestCase("SELECT customer_id, COUNT(*) AS n FROM shop.[order] GROUP BY customer_id WITH ROLLUP")]
    [TestCase("SELECT customer_id, order_date, COUNT(*) AS n FROM shop.[order] GROUP BY GROUPING SETS ((customer_id), (order_date), ())")]
    [TestCase("SELECT customer_id, YEAR(order_date) AS y, GROUPING(customer_id) AS g, COUNT(*) AS n FROM shop.[order] GROUP BY CUBE (customer_id, YEAR(order_date))")]
    [TestCase("SELECT CustomerCode FROM shop.customer UNION SELECT name FROM shop.product ORDER BY CustomerCode")]
    [TestCase("SELECT CustomerCode FROM shop.customer UNION ALL SELECT name FROM shop.product ORDER BY 1 DESC")]
    [TestCase("SELECT customer_id FROM shop.customer EXCEPT SELECT customer_id FROM shop.[order]")]
    [TestCase("SELECT customer_id FROM shop.customer INTERSECT SELECT customer_id FROM shop.[order]")]
    [TestCase("SELECT name FROM shop.customer c WHERE EXISTS (SELECT 1 FROM shop.[order] o WHERE o.customer_id = c.customer_id)")]
    [TestCase("SELECT name FROM shop.customer c WHERE c.customer_id NOT IN (SELECT customer_id FROM shop.[order])")]
    [TestCase("SELECT name FROM shop.customer c WHERE c.customer_id >= ALL (SELECT customer_id FROM shop.customer)")]
    [TestCase("SELECT name FROM shop.customer c WHERE c.customer_id = ANY (SELECT customer_id FROM shop.[order] WHERE order_date > '2024-01-31')")]
    [TestCase("SELECT c.name, (SELECT COUNT(*) FROM shop.[order] o WHERE o.customer_id = c.customer_id) AS orders FROM shop.customer c")]
    [TestCase("SELECT order_id, ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY order_date) AS rn, SUM(order_id) OVER () AS total FROM shop.[order]")]
    [TestCase("SELECT v.a, v.b FROM (VALUES (1, N'x'), (2, N'y')) AS v (a, b)")]
    [TestCase("SELECT value FROM STRING_SPLIT(N'a,b,c', ',')")]
    [TestCase("SELECT name FROM shop.customer WITH (NOLOCK) WHERE is_active = 1 OPTION (RECOMPILE)")]
    [TestCase("SELECT c.name, STUFF((SELECT ',' + CAST(o.order_id AS varchar(10)) FROM shop.[order] o WHERE o.customer_id = c.customer_id ORDER BY o.order_id FOR XML PATH('')), 1, 1, '') AS orders FROM shop.customer c")]
    [TestCase("SELECT STUFF((SELECT ', ' + name FROM shop.customer ORDER BY name FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS names")]
    [TestCase("SELECT (SELECT N'<' + name + N'&' FROM shop.customer ORDER BY name FOR XML PATH('')) AS escaped")]
    [TestCase("SELECT * FROM (SELECT customer_id, YEAR(order_date) AS y FROM shop.[order]) s PIVOT (COUNT(y) FOR y IN ([2023], [2024])) p")]
    [TestCase("SELECT sale_id, k, v FROM (SELECT sale_id, amount AS a, CAST(sale_id AS decimal(12,2)) AS b FROM shop.sales_fact) s UNPIVOT (v FOR k IN (a, b)) u")]
    [TestCase("SELECT l.order_id, SUM(l.qty * l.unit_price) AS amount FROM shop.order_line l GROUP BY l.order_id HAVING SUM(l.qty) >= 2 ORDER BY amount DESC")]
    public Task Queries(string tsql) => Same(tsql);

    [TestCase("SELECT customer_id AS id, name AS [info.name], email AS [info.email] FROM shop.customer ORDER BY customer_id FOR JSON PATH, ROOT('customers')")]
    [TestCase("SELECT name, email FROM shop.customer ORDER BY name FOR JSON PATH, INCLUDE_NULL_VALUES")]
    [TestCase("SELECT name FROM shop.customer WHERE customer_id = 100 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER")]
    [TestCase("SELECT o.order_id, (SELECT l.line_no, l.qty FROM shop.order_line l WHERE l.order_id = o.order_id ORDER BY l.line_no FOR JSON PATH) AS lines FROM shop.[order] o ORDER BY o.order_id FOR JSON PATH")]
    public Task Json(string tsql) => Same(tsql, json: true);

    [Test]
    public Task SelectIntoTempTable() =>
        Same("SELECT customer_id, name INTO #c FROM shop.customer WHERE is_active = 1; SELECT name FROM #c ORDER BY name;");

    [TestCase("INSERT INTO shop.product (product_id, name, parent_id) VALUES (10, N'new', 1), (11, N'other', NULL)", "SELECT * FROM shop.product")]
    [TestCase("INSERT INTO shop.product (product_id, name) SELECT customer_id + 1000, name FROM shop.customer WHERE is_active = 1", "SELECT * FROM shop.product")]
    [TestCase("INSERT INTO shop.customer (CustomerCode, name, email, is_active) VALUES ('C8', N'yan', N'y@x', 0)", "SELECT CustomerCode, name, is_active FROM shop.customer")]
    [TestCase("INSERT INTO shop.sales_fact VALUES (3, 1.5)", "SELECT * FROM shop.sales_fact")]
    [TestCase("UPDATE shop.customer SET is_active = 0, email = NULL WHERE name = N'bob'", "SELECT name, email, is_active FROM shop.customer")]
    [TestCase("UPDATE shop.order_line SET qty += 1, unit_price *= 2 WHERE order_id = 1", "SELECT * FROM shop.order_line")]
    [TestCase("UPDATE o SET total = 1 FROM shop.[order] o JOIN shop.customer c ON c.customer_id = o.customer_id WHERE c.name = N'alice'", "SELECT order_id, total FROM shop.[order]")]
    [TestCase("UPDATE shop.[order] SET total = l.qty FROM shop.order_line l WHERE l.order_id = shop.[order].order_id AND l.line_no = 1", "SELECT order_id, total FROM shop.[order]")]
    [TestCase("DELETE FROM shop.sales_fact WHERE sale_id = 1", "SELECT * FROM shop.sales_fact")]
    [TestCase("DELETE l FROM shop.order_line l JOIN shop.[order] o ON o.order_id = l.order_id WHERE o.customer_id = 100", "SELECT order_id, line_no FROM shop.order_line")]
    [TestCase("MERGE shop.product AS t USING (VALUES (2, N'child a2'), (20, N'fresh')) AS s (id, name) ON t.product_id = s.id WHEN MATCHED THEN UPDATE SET name = s.name WHEN NOT MATCHED THEN INSERT (product_id, name) VALUES (s.id, s.name) WHEN NOT MATCHED BY SOURCE AND t.product_id = 5 THEN DELETE;", "SELECT * FROM shop.product")]
    public Task Dml(string dml, string select) => SameAfter(dml, select);

    private const string Numbers = "(VALUES (1, CAST(0.0001 AS money), CAST(0.0002 AS decimal(18,4)), CAST(0.1 AS real), CAST(1 AS smallint)), "
        + "(2, CAST(0.0001 AS money), CAST(0.0000 AS decimal(18,4)), CAST(0.25 AS real), CAST(2 AS smallint)), "
        + "(3, CAST(0.0000 AS money), CAST(0.0000 AS decimal(18,4)), CAST(0.1 AS real), CAST(2 AS smallint))) AS t (id, m, d, r, i)";

    /// <summary>Aggregate result types (AVG of money is money, of decimal(18,4) is decimal(38,6)) and real against exact numbers.</summary>
    [TestCase("SELECT AVG(m) AS m, AVG(d) AS d, AVG(i) AS i, SUM(m) AS sm, SUM(d) AS sd FROM {0}")]
    [TestCase("SELECT AVG(-m) AS m, AVG(-d) AS d FROM {0}")]
    [TestCase("SELECT AVG(r) AS r, SUM(r) AS s FROM {0}")]
    [TestCase("SELECT id FROM {0} WHERE r > 0.1")]
    [TestCase("SELECT id FROM {0} WHERE r = 0.1")]
    [TestCase("SELECT id FROM {0} WHERE r BETWEEN 0.1 AND 0.2")]
    [TestCase("SELECT id, r * 1.5 AS x, r + i AS s FROM {0}")]
    [TestCase("SELECT id, CAST(r * 14411.37 AS money) AS m, CONVERT(decimal(18,6), r / 3) AS d FROM {0}")]
    [TestCase("SELECT id, AVG(m) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS a, AVG(i) OVER () AS ai FROM {0}")]
    public Task Numeric(string select) => Same(string.Format(select, Numbers));

    [TestCase("INSERT INTO shop.product (product_id, name) OUTPUT inserted.product_id, inserted.name VALUES (12, N'out')")]
    [TestCase("UPDATE shop.[order] SET total = total + 1 OUTPUT deleted.total AS before, inserted.total AS after WHERE order_id = 1")]
    [TestCase("DELETE FROM shop.sales_fact OUTPUT deleted.sale_id, deleted.amount WHERE amount > 15")]
    [TestCase("MERGE shop.product AS t USING (VALUES (2, N'child a2'), (21, N'new')) AS s (id, name) ON t.product_id = s.id WHEN MATCHED THEN UPDATE SET name = s.name WHEN NOT MATCHED THEN INSERT (product_id, name) VALUES (s.id, s.name) OUTPUT $action AS act, inserted.product_id;")]
    public Task Returning(string dml) => Same(dml);
}
