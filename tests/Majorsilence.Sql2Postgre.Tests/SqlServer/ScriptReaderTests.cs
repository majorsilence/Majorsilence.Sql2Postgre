using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Tests.SqlServer;

public class ScriptReaderTests
{
    private static SourceReadResult Read(string script) => SqlServerScriptReader.Read([("test.sql", script)]);

    private static SourceDatabase Shop() => Read(File.ReadAllText(
        Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql"))).Database;

    [Test]
    public void ShopFixtureReadsWithoutDiagnostics()
    {
        var result = Read(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")));
        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(result.Database.Tables.Select(t => t.Name.Name),
                Is.EqualTo(new[] { "all_types", "customer", "order", "order_line", "product", "sales_fact" }));
            Assert.That(result.Database.Schemas, Is.EqualTo(new[] { "dbo", "shop" }));
            Assert.That(result.Database.Modules.Select(m => m.Name.Name), Is.EqualTo(new[]
                { "active_customer", "add_order", "customer_orders", "line_amount", "secret_proc", "trg_order_line_total" }));
        });
    }

    [Test]
    public void UnqualifiedNamesAreInDbo() =>
        Assert.That(Read("CREATE TABLE t (a int);").Database.Tables.Single().Name, Is.EqualTo(new ObjectName("dbo", "t")));

    [TestCase("varchar", "varchar", 1, null, null)]
    [TestCase("nvarchar(max)", "nvarchar", -1, null, null)]
    [TestCase("decimal", "decimal", null, 18, 0)]
    [TestCase("numeric(9)", "numeric", null, 9, 0)]
    [TestCase("float", "float", null, 53, null)]
    [TestCase("float(10)", "real", null, 24, null)]
    [TestCase("datetime2", "datetime2", null, null, 7)]
    [TestCase("time(0)", "time", null, null, 0)]
    [TestCase("rowversion", "timestamp", null, null, null)]
    [TestCase("sysname", "sysname", null, null, null)]
    [TestCase("geography", "geography", null, null, null)]
    [TestCase("xml", "xml", null, null, null)]
    [TestCase("bit", "bit", null, null, null)]
    public void TypesAreFilledInAsSqlServerDoes(string declared, string name, int? length, int? precision, int? scale)
    {
        var type = Read($"CREATE TABLE t (c {declared});").Database.Tables.Single().Columns.Single().Type;
        Assert.That(type, Is.EqualTo(new SqlServerType(name, length, precision, scale)));
    }

    [Test]
    public void AliasTypeDefinedInALaterFileResolves()
    {
        var db = SqlServerScriptReader.Read([
            ("1.sql", "CREATE TABLE t (c dbo.code);"),
            ("2.sql", "CREATE TYPE dbo.code FROM varchar(12) NOT NULL;"),
        ]).Database;
        Assert.That(db.Tables.Single().Columns.Single().Type, Is.EqualTo(new SqlServerType("varchar", 12, UserTypeName: "dbo.code")));
    }

    [Test]
    public void NullabilityDefaults()
    {
        var t = Read("CREATE TABLE t (id int IDENTITY PRIMARY KEY, a int, b int NOT NULL, v rowversion, k int, CONSTRAINT pk2 UNIQUE (k));")
            .Database.Tables.Single();
        Assert.That(t.Columns.Select(c => c.IsNullable), Is.EqualTo(new[] { false, true, false, false, true }));
    }

    [Test]
    public void PrimaryKeyIsClusteredUnlessSaidOtherwise()
    {
        var db = Read("CREATE TABLE a (id int PRIMARY KEY); CREATE TABLE b (id int, CONSTRAINT pk_b PRIMARY KEY NONCLUSTERED (id));").Database;
        Assert.Multiple(() =>
        {
            Assert.That(db.Tables[0].PrimaryKey, Is.EqualTo(db.Tables[0].PrimaryKey! with { Name = null, IsClustered = true }));
            Assert.That(db.Tables[1].PrimaryKey!.Name, Is.EqualTo("pk_b"));
            Assert.That(db.Tables[1].PrimaryKey!.IsClustered, Is.False);
        });
    }

    [Test]
    public void ConstraintsAddedLaterAndDisabled()
    {
        var t = Shop().Tables.Single(x => x.Name.Name == "order_line");
        var fk = t.ForeignKeys.Single(f => f.Name == "fk_line_product");
        var customer = Shop().Tables.Single(x => x.Name.Name == "customer");
        Assert.Multiple(() =>
        {
            Assert.That(fk.IsNotTrusted, Is.True);
            Assert.That(fk.IsDisabled, Is.False);
            Assert.That(fk.OnDelete, Is.EqualTo(ReferentialAction.SetNull));
            Assert.That(customer.Checks.Single().IsDisabled, Is.True);
            Assert.That(customer.Checks.Single().Expression, Is.EqualTo("email LIKE '%@%'"));
        });
    }

    [Test]
    public void IdentityAndDefaults()
    {
        var customer = Shop().Tables.Single(x => x.Name.Name == "customer");
        var id = customer.Columns.Single(c => c.Name == "customer_id");
        var created = customer.Columns.Single(c => c.Name == "created_at");
        Assert.Multiple(() =>
        {
            Assert.That(id.Identity, Is.EqualTo(new IdentitySpec(100, 5)));
            Assert.That(created.DefaultExpression, Is.EqualTo("(sysdatetime())"));
            Assert.That(created.DefaultConstraintName, Is.EqualTo("df_customer_created"));
        });
    }

    [Test]
    public void SequenceStartsAtItsMinimumWhenStartIsMissing()
    {
        var s = Read("CREATE SEQUENCE dbo.s AS int MINVALUE 5;").Database.Sequences.Single();
        Assert.That(s, Is.EqualTo(new Sequence(new ObjectName("dbo", "s"), new SqlServerType("int"), 5, 1, 5, int.MaxValue, false)));
    }

    [Test]
    public void ParseErrorIsReportedAndTheNextBatchStillRead()
    {
        var result = Read("CREATE TABLE a (id int);\nGO\nCREATE TABLE b (id int,,);\nGO\nCREATE TABLE c (id int);");
        Assert.Multiple(() =>
        {
            Assert.That(result.Database.Tables.Select(t => t.Name.Name), Is.EqualTo(new[] { "a", "c" }));
            var d = result.Diagnostics.Single();
            Assert.That(d.Code, Is.EqualTo(DiagnosticCodes.ParseError));
            Assert.That(d.Span!.Line, Is.EqualTo(3));
            Assert.That(d.Message, Does.StartWith("test.sql(3,"));
        });
    }

    [Test]
    public void TriggerRecordsItsTable()
    {
        var m = Shop().Modules.Single(x => x.Kind == ObjectKind.Trigger);
        Assert.That(m.ParentTable, Is.EqualTo(new ObjectName("shop", "order_line")));
        Assert.That(m.Definition, Does.StartWith("CREATE TRIGGER shop.trg_order_line_total"));
    }
}
