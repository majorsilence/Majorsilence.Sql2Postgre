using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;

namespace Majorsilence.Sql2Postgre.Tests.Translation;

public class NameMapperTests
{
    private static readonly ObjectName Orders = new("dbo", "Orders");

    private static NameMapper Mapper(NameStyle style = NameStyle.Lower) => new(new ConversionOptions { NameStyle = style });

    [TestCase("OrderID", NameStyle.Lower, "orderid")]
    [TestCase("OrderID", NameStyle.SnakeCase, "order_id")]
    [TestCase("HTTPServer", NameStyle.SnakeCase, "http_server")]
    [TestCase("CustomerCode", NameStyle.SnakeCase, "customer_code")]
    [TestCase("Address2Line", NameStyle.SnakeCase, "address2_line")]
    [TestCase("Order Details", NameStyle.SnakeCase, "order_details")]
    [TestCase("already_snake", NameStyle.SnakeCase, "already_snake")]
    [TestCase("ID", NameStyle.SnakeCase, "id")]
    [TestCase("__weird--name__", NameStyle.SnakeCase, "weird_name")]
    [TestCase("OrderID", NameStyle.Preserve, "OrderID")]
    public void Styles(string source, NameStyle style, string expected) =>
        Assert.That(Mapper(style).Column(Orders, source), Is.EqualTo(expected));

    [TestCase("customer", "customer")]
    [TestCase("user", "\"user\"")]
    [TestCase("order", "\"order\"")]
    [TestCase("select", "\"select\"")]
    [TestCase("between", "\"between\"")]
    [TestCase("left", "\"left\"")]
    [TestCase("OrderID", "\"OrderID\"")]
    [TestCase("1st", "\"1st\"")]
    [TestCase("a$b", "a$b")]
    [TestCase("my\"name", "\"my\"\"name\"")]
    [TestCase("Order Details", "\"Order Details\"")]
    public void QuotesOnlyWhenNeeded(string identifier, string expected) =>
        Assert.That(NameMapper.Quote(identifier), Is.EqualTo(expected));

    [Test]
    public void SchemaMapAppliesFirstAndVerbatim()
    {
        var mapper = new NameMapper(new ConversionOptions
        {
            SchemaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["dbo"] = "public", ["Sales"] = "Revenue" },
        });
        Assert.Multiple(() =>
        {
            Assert.That(mapper.Relation(new ObjectName("dbo", "Orders")), Is.EqualTo(new PgName("public", "orders")));
            Assert.That(mapper.Relation(new ObjectName("SALES", "x")).Schema, Is.EqualTo("Revenue"));
            Assert.That(mapper.Relation(new ObjectName("HR", "x")).Schema, Is.EqualTo("hr"));
            Assert.That(mapper.Relation(new ObjectName("dbo", "Order")).Qualified, Is.EqualTo("public.\"order\""));
        });
    }

    [Test]
    public void LongNamesAreTruncatedWithAHash()
    {
        var mapper = Mapper();
        string source = new('a', 70);
        string mapped = mapper.Column(Orders, source);
        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetByteCount(mapped), Is.EqualTo(63));
            Assert.That(mapped, Does.Match("^a{54}_[0-9a-f]{8}$"));
            Assert.That(mapper.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.IdentifierTruncated));
            Assert.That(mapper.Column(Orders, new string('a', 71)), Is.Not.EqualTo(mapped), "different long names stay different");
        });
    }

    [Test]
    public void TruncationNeverSplitsAMultibyteCharacter()
    {
        string source = new('é', 40); // 80 bytes
        string mapped = Mapper().Column(Orders, source);
        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetByteCount(mapped), Is.LessThanOrEqualTo(63));
            Assert.That(mapped, Does.Match("^é{27}_[0-9a-f]{8}$"));
        });
    }

    [Test]
    public void ExactlySixtyThreeBytesIsKept() =>
        Assert.That(Mapper().Column(Orders, new string('b', 63)), Is.EqualTo(new string('b', 63)));

    [Test]
    public void CollisionsGetASuffix()
    {
        var mapper = Mapper(NameStyle.SnakeCase);
        Assert.Multiple(() =>
        {
            Assert.That(mapper.Column(Orders, "OrderID"), Is.EqualTo("order_id"));
            Assert.That(mapper.Column(Orders, "Order_ID"), Is.EqualTo("order_id_2"));
            Assert.That(mapper.Column(Orders, "ORDER-ID"), Is.EqualTo("order_id_3"));
            Assert.That(mapper.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCodes.IdentifierCollision, DiagnosticCodes.IdentifierCollision }));
        });
    }

    [Test]
    public void IndexesShareTheTableNamespace()
    {
        var mapper = Mapper();
        Assert.That(mapper.Relation(new ObjectName("dbo", "Customer")).Name, Is.EqualTo("customer"));
        Assert.That(mapper.Index(Orders, "CUSTOMER"), Is.EqualTo("customer_2"));
    }

    [Test]
    public void ColumnsInDifferentTablesDoNotCollide()
    {
        var mapper = Mapper();
        Assert.That(mapper.Column(Orders, "Id"), Is.EqualTo("id"));
        Assert.That(mapper.Column(new ObjectName("dbo", "Lines"), "ID"), Is.EqualTo("id"));
        Assert.That(mapper.Diagnostics, Is.Empty);
    }

    [Test]
    public void SameSourceNameInAnyCaseGetsTheSameAnswer()
    {
        var mapper = Mapper(NameStyle.Preserve);
        string first = mapper.Column(Orders, "OrderDate");
        Assert.Multiple(() =>
        {
            Assert.That(mapper.Column(Orders, "orderdate"), Is.EqualTo(first));
            Assert.That(mapper.Column(Orders, "ORDERDATE"), Is.EqualTo("OrderDate"));
            Assert.That(mapper.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void KeywordListIsLoaded() =>
        Assert.That(NameMapper.NonUnreservedKeywords, Has.Count.GreaterThan(150).And.Contains("select").And.Contains("xmltable"));
}
