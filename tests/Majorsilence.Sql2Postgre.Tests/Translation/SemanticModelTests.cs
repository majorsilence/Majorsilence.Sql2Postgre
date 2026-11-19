using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Tests.Translation;

public class SemanticModelTests
{
    private static SemanticModel Model(string script) => SemanticModel.Build(SqlServerScriptReader.Read([("test.sql", script)]).Database);

    private static SemanticModel Shop() => Model(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")));

    private static IReadOnlyList<ColumnInfo> Columns(SemanticModel model, string select, string schema = "dbo")
    {
        var statement = (SelectStatement)((TSqlScript)new TSql160Parser(true).Parse(new StringReader(select), out var errors)).Batches[0].Statements[0];
        Assert.That(errors, Is.Empty);
        var scope = new Scope();
        model.DefineCtes(statement.WithCtesAndXmlNamespaces, scope, schema);
        return model.OutputColumns(statement.QueryExpression, scope, schema);
    }

    private static string? T(ColumnInfo c) => c.Type?.ToString();

    [Test]
    public void ViewColumnsAreInferredFromTheirSelect()
    {
        var view = Shop().Relation(new ObjectName("shop", "active_customer"))!;
        Assert.That(view.Kind, Is.EqualTo(ObjectKind.View));
        Assert.That(view.Columns.Select(c => (c.Name, T(c))), Is.EqualTo(new[] { ("customer_id", "int"), ("name", "nvarchar(100)") }));
    }

    [Test]
    public void RoutineSignatures()
    {
        var model = Shop();
        var scalar = model.Routine(new ObjectName("shop", "line_amount"))!;
        var inline = model.Routine(new ObjectName("shop", "customer_orders"))!;
        var proc = model.Routine(new ObjectName("shop", "add_order"))!;
        Assert.Multiple(() =>
        {
            Assert.That(scalar.ReturnType, Is.EqualTo(new SqlServerType("decimal", Precision: 12, Scale: 2)));
            Assert.That(scalar.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "qty", "price" }));
            Assert.That(inline.IsTableValued, Is.True);
            Assert.That(inline.ResultColumns!.Select(c => (c.Name, T(c))), Is.EqualTo(new[] { ("order_id", "bigint"), ("order_date", "date") }));
            Assert.That(proc.Kind, Is.EqualTo(ObjectKind.Procedure));
            Assert.That(proc.Parameters.Select(p => (p.Name, p.IsOutput)), Is.EqualTo(new[] { ("customer_id", false), ("order_id", true) }));
        });
    }

    [Test]
    public void MultiStatementTvfColumnsComeFromItsTableDeclaration()
    {
        var model = Model("""
            CREATE TABLE dbo.t (id int NOT NULL);
            GO
            CREATE FUNCTION dbo.f(@n int) RETURNS @r TABLE (id int, label nvarchar(20)) AS BEGIN INSERT @r SELECT id, N'x' FROM dbo.t; RETURN; END;
            """);
        Assert.That(model.Routine(new ObjectName("dbo", "f"))!.ResultColumns!.Select(c => (c.Name, T(c))),
            Is.EqualTo(new[] { ("id", "int"), ("label", "nvarchar(20)") }));
    }

    [Test]
    public void DependenciesComeFromTheParsedBodies()
    {
        var deps = Shop().Dependencies;
        ObjectDependency D(string from, string to) => new(new ObjectName("shop", from), new ObjectName("shop", to));
        Assert.That(deps, Is.SupersetOf(new[]
        {
            D("active_customer", "customer"),
            D("customer_orders", "order"),
            D("add_order", "order"),
            D("trg_order_line_total", "order"),
            D("trg_order_line_total", "order_line"),
        }));
        Assert.That(deps.Where(d => d.From == d.To), Is.Empty);
    }

    [Test]
    public void ExpressionTypesFollowSqlServerPrecedence()
    {
        var cols = Columns(Shop(), """
            SELECT c.name + N'!' AS a, o.total * 2 AS b, CASE WHEN o.order_id > 1 THEN 1 ELSE 2.5 END AS c,
                   COUNT(*) AS d, COUNT_BIG(*) AS e, LEN(c.name) AS f, ISNULL(c.email, 'x') AS g,
                   (SELECT MAX(x.order_date) FROM shop.[order] x) AS h, CAST(o.total AS varchar(10)) AS i,
                   o.order_id + 1.5 AS j, c.customer_id + 1 AS k, GETDATE() AS l, NULL AS m, '5' + 1 AS n
            FROM shop.customer c JOIN shop.[order] o ON o.customer_id = c.customer_id
            """, "shop");
        Assert.That(cols.Select(T), Is.EqualTo(new string?[]
        {
            "nvarchar(max)", "money", "decimal", "int", "bigint", "int", "nvarchar(200)",
            "date", "varchar(10)", "decimal", "int", "datetime", null, "int",
        }));
    }

    [Test]
    public void CaseBranchesCombine()
    {
        var cols = Columns(Shop(), "SELECT CASE WHEN 1 = 1 THEN 'ab' ELSE 'abcdef' END AS s, CASE WHEN 1 = 1 THEN N'a' ELSE 'abc' END AS u, CASE WHEN 1 = 1 THEN 1.5 ELSE 2.25 END AS d FROM shop.product");
        // Strings of different types: the higher precedence type with the longer length (the result holds 'abc').
        Assert.That(cols.Select(T), Is.EqualTo(new[] { "varchar(6)", "nvarchar(3)", "decimal" }));
    }

    [Test]
    public void DerivedTableColumnShadowsTheTablesColumn()
    {
        var cols = Columns(Shop(), "SELECT x.name FROM (SELECT customer_id AS name FROM shop.customer) x");
        Assert.That(cols.Single().Type, Is.EqualTo(new SqlServerType("int")));
    }

    [Test]
    public void DerivedTableColumnListRenames()
    {
        var cols = Columns(Shop(), "SELECT * FROM (SELECT customer_id, name FROM shop.customer) AS x (id, label)");
        Assert.That(cols.Select(c => (c.Name, T(c))), Is.EqualTo(new[] { ("id", "int"), ("label", "nvarchar(100)") }));
    }

    [Test]
    public void CorrelatedSubqueryResolvesTheOuterColumn()
    {
        var cols = Columns(Shop(), "SELECT (SELECT TOP 1 c.name FROM shop.[order] o WHERE o.customer_id = c.customer_id) AS n FROM shop.customer c");
        Assert.That(cols.Single().Type?.ToString(), Is.EqualTo("nvarchar(100)"));
    }

    [Test]
    public void AmbiguousUnqualifiedColumnHasNoType()
    {
        var cols = Columns(Shop(), "SELECT customer_id FROM shop.customer c JOIN shop.[order] o ON o.customer_id = c.customer_id");
        Assert.That(cols.Single().Type, Is.Null);
    }

    [Test]
    public void StarExpandsEveryBindingOrOne()
    {
        var model = Shop();
        Assert.That(Columns(model, "SELECT * FROM shop.product").Select(c => c.Name), Is.EqualTo(new[] { "product_id", "name", "parent_id" }));
        Assert.That(Columns(model, "SELECT p.* , o.order_id FROM shop.product p CROSS JOIN shop.[order] o").Select(c => c.Name),
            Is.EqualTo(new[] { "product_id", "name", "parent_id", "order_id" }));
    }

    [Test]
    public void CtesAreVisibleIncludingRecursiveOnes()
    {
        var cols = Columns(Shop(), """
            WITH tree (id, depth) AS (
                SELECT product_id, 0 FROM shop.product WHERE parent_id IS NULL
                UNION ALL
                SELECT p.product_id, t.depth + 1 FROM shop.product p JOIN tree t ON p.parent_id = t.id)
            SELECT id, depth FROM tree
            """);
        Assert.That(cols.Select(c => (c.Name, T(c))), Is.EqualTo(new[] { ("id", "int"), ("depth", "int") }));
    }

    [Test]
    public void TableValuedFunctionInFromAndApply()
    {
        var cols = Columns(Shop(), "SELECT c.name, o.order_date FROM shop.customer c CROSS APPLY shop.customer_orders(c.customer_id) o");
        Assert.That(cols.Select(T), Is.EqualTo(new[] { "nvarchar(100)", "date" }));
    }

    [Test]
    public void ValuesTableHasItsColumnsAndTypes()
    {
        var cols = Columns(Shop(), "SELECT v.a, v.b FROM (VALUES (1, N'x'), (2, N'y')) AS v (a, b)");
        Assert.That(cols.Select(T), Is.EqualTo(new[] { "int", "nvarchar(1)" }));
    }

    [Test]
    public void VariableInANestedScopeShadowsTheOuterOne()
    {
        var model = Shop();
        var outer = new Scope();
        outer.DefineVariable("@x", new SqlServerType("int"));
        var inner = outer.Child();
        inner.DefineVariable("x", new SqlServerType("varchar", 10));
        var reference = new VariableReference { Name = "@x" };
        Assert.Multiple(() =>
        {
            Assert.That(model.TypeOf(reference, inner), Is.EqualTo(new SqlServerType("varchar", 10)));
            Assert.That(model.TypeOf(reference, outer), Is.EqualTo(new SqlServerType("int")));
            Assert.That(model.TypeOf(new VariableReference { Name = "@missing" }, inner), Is.Null);
        });
    }

    [Test]
    public void UnqualifiedNamesResolveInTheModulesSchemaThenDbo()
    {
        var model = Model("""
            CREATE SCHEMA app;
            GO
            CREATE TABLE app.item (a int NOT NULL);
            CREATE TABLE dbo.item (b nvarchar(5) NOT NULL);
            CREATE TABLE dbo.only_dbo (c bit NOT NULL);
            GO
            CREATE VIEW app.v AS SELECT i.a, o.c FROM item i CROSS JOIN only_dbo o;
            """);
        var view = model.Relation(new ObjectName("app", "v"))!;
        Assert.That(view.Columns.Select(T), Is.EqualTo(new[] { "int", "bit" }));
        Assert.That(model.Dependencies.Select(d => d.To.ToString()), Is.SupersetOf(new[] { "app.item", "dbo.only_dbo" }));
    }

    [Test]
    public void ExternalReferencesAreReported()
    {
        var model = Model("""
            CREATE TABLE dbo.t (id int NOT NULL);
            GO
            CREATE VIEW dbo.v AS SELECT a.id FROM otherdb.dbo.t a JOIN script.dbo.t b ON a.id = b.id JOIN srv.db.dbo.t c ON c.id = a.id;
            """);
        var external = model.Diagnostics.Where(d => d.Code == DiagnosticCodes.ExternalReference).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(external, Has.Count.EqualTo(2), "otherdb and srv; 'script' is this database's own name");
            Assert.That(external.All(d => d.Severity == Severity.Error), Is.True);
            Assert.That(external.Select(d => d.Message), Has.Some.Contains("otherdb.dbo.t").And.Some.Contains("srv.db.dbo.t"));
        });
    }

    [Test]
    public void ColumnMethodCallsAreNotExternalReferences()
    {
        var model = Model("""
            CREATE TABLE dbo.t (id int NOT NULL, doc xml NULL);
            GO
            CREATE VIEW dbo.v AS SELECT x.doc.value('(/a)[1]', 'int') AS a FROM dbo.t x;
            GO
            CREATE VIEW dbo.w AS SELECT n.c.value('.', 'int') AS v FROM dbo.t x CROSS APPLY x.doc.nodes('/a/b') AS n(c);
            """);
        Assert.That(model.Diagnostics, Is.Empty);
    }

    [Test]
    public void AliasTypeParametersResolveToTheirBase()
    {
        var model = Model("""
            CREATE TYPE dbo.code FROM char(3) NOT NULL;
            GO
            CREATE FUNCTION dbo.f(@c dbo.code) RETURNS dbo.code AS BEGIN RETURN @c; END;
            """);
        var f = model.Routine(new ObjectName("dbo", "f"))!;
        Assert.That(f.ReturnType, Is.EqualTo(new SqlServerType("char", 3, UserTypeName: "dbo.code")));
        Assert.That(f.Parameters.Single().Type.Name, Is.EqualTo("char"));
    }

    [Test]
    public void ViewOverAViewResolves()
    {
        var model = Model("""
            CREATE TABLE dbo.t (id int NOT NULL, n money NOT NULL);
            GO
            CREATE VIEW dbo.v2 AS SELECT id, total FROM dbo.v1;
            GO
            CREATE VIEW dbo.v1 AS SELECT id, n * 2 AS total FROM dbo.t;
            """);
        Assert.That(model.Relation(new ObjectName("dbo", "v2"))!.Columns.Select(T), Is.EqualTo(new[] { "int", "money" }));
    }
}
