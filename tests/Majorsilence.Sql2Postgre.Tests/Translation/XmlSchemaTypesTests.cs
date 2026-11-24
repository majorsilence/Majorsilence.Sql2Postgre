using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Tests.Translation;

/// <summary>WP 4.24: the element types of an XML schema collection, as XML_SCHEMA_NAMESPACE returns its documents.</summary>
public class XmlSchemaTypesTests
{
    // Two schema documents one after the other, the second importing the first (no
    // schemaLocation), with element-only content written as a restriction of anyType, as SQL
    // Server returns them.
    private const string Schemas = """
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:a" targetNamespace="urn:a" elementFormDefault="qualified"><xsd:element name="Note" type="xsd:string"/><xsd:element name="Amount" type="t:Money"/><xsd:simpleType name="Money"><xsd:restriction base="xsd:decimal"><xsd:fractionDigits value="2"/></xsd:restriction></xsd:simpleType></xsd:schema><xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:a="urn:a" xmlns:t="urn:s" targetNamespace="urn:s" elementFormDefault="qualified"><xsd:import namespace="urn:a"/><xsd:element name="Survey"><xsd:complexType><xsd:complexContent><xsd:restriction base="xsd:anyType"><xsd:sequence><xsd:element name="Total" type="xsd:decimal" minOccurs="0"/><xsd:element name="Children" type="xsd:int" minOccurs="0"/><xsd:element name="Ratio" type="xsd:double" minOccurs="0"/><xsd:element name="Born" type="xsd:date" minOccurs="0"/><xsd:element name="Hobby" type="t:HobbyType" minOccurs="0" maxOccurs="unbounded"/><xsd:element ref="a:Note" minOccurs="0"/><xsd:element name="Same" type="xsd:string" minOccurs="0"/><xsd:element name="Nested" minOccurs="0"><xsd:complexType><xsd:sequence><xsd:element name="Same" type="xsd:int"/></xsd:sequence></xsd:complexType></xsd:element></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element><xsd:simpleType name="HobbyType"><xsd:restriction base="xsd:string"><xsd:enumeration value="Bikes"/></xsd:restriction></xsd:simpleType></xsd:schema>
        """;

    [TestCase("urn:s", "Total", XQueryType.Decimal)]
    [TestCase("urn:s", "Children", XQueryType.Integer)]
    [TestCase("urn:s", "Ratio", XQueryType.Double)]
    [TestCase("urn:s", "Born", XQueryType.Other)]
    [TestCase("urn:s", "Hobby", XQueryType.String)]
    [TestCase("urn:a", "Note", XQueryType.String)]
    [TestCase("urn:a", "Amount", XQueryType.Decimal)]
    public void ElementTypes(string ns, string name, XQueryType expected) =>
        Assert.That(XmlSchemaTypes.Read(Schemas)!.Element(ns, name), Is.EqualTo(expected));

    [TestCase("urn:s", "Survey")]
    [TestCase("urn:s", "Nested")]
    [TestCase("urn:s", "Same")]
    [TestCase("urn:s", "Missing")]
    [TestCase("", "Total")]
    public void ElementsWithoutOneSimpleType(string ns, string name) =>
        Assert.That(XmlSchemaTypes.Read(Schemas)!.Element(ns, name), Is.Null);

    [Test]
    public void UnreadableSchemasHaveNoTypes() =>
        Assert.That(XmlSchemaTypes.Read("<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"a\" type=\"nope\"/></xsd:schema>"), Is.Null);
}
