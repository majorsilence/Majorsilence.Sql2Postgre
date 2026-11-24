using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Majorsilence.Sql2Postgre.Translation.Semantics;

/// <summary>The XQuery type of a value, as far as the xml <c>modify()</c> translation needs it.</summary>
public enum XQueryType
{
    /// <summary>xdt:untypedAtomic: the value of a node in untyped xml (xs:double in arithmetic).</summary>
    Untyped,
    String,
    Integer,
    Decimal,
    Double,
    Boolean,
    /// <summary>Any other type (dates, xs:float, binary…).</summary>
    Other,
}

/// <summary>
/// The element types of an XML schema collection, read from its XML Schema documents with
/// System.Xml's schema compiler: every element declaration, global or local, by its qualified name.
/// A name declared with two different types has no single type.
/// </summary>
public sealed class XmlSchemaTypes
{
    private readonly Dictionary<(string Namespace, string Name), XQueryType?> _elements = [];

    private XmlSchemaTypes()
    {
    }

    /// <summary>The types, or null when the documents cannot be read or compiled.</summary>
    public static XmlSchemaTypes? Read(string schemas)
    {
        try
        {
            var set = new XmlSchemaSet { XmlResolver = null };
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader("<schemas>" + schemas + "</schemas>"), settings))
            {
                foreach (var schema in XElement.Load(reader).Elements(XName.Get("schema", XmlSchema.Namespace)))
                {
                    using var schemaReader = schema.CreateReader();
                    set.Add(XmlSchema.Read(schemaReader, null)!);
                }
            }
            set.Compile();
            var types = new XmlSchemaTypes();
            var seen = new HashSet<XmlSchemaType>();
            foreach (XmlSchemaElement element in set.GlobalElements.Values)
                types.Visit(element, seen);
            foreach (XmlSchemaType type in set.GlobalTypes.Values)
                types.Visit(type, seen);
            return types;
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The type of the element with this namespace and local name; null when it is not declared or has no single type.</summary>
    public XQueryType? Element(string ns, string name) => _elements.GetValueOrDefault((ns, name));

    private void Visit(XmlSchemaElement element, HashSet<XmlSchemaType> seen)
    {
        var key = (element.QualifiedName.Namespace, element.QualifiedName.Name);
        var type = TypeOf(element.ElementSchemaType);
        if (_elements.TryGetValue(key, out var known) && known != type)
            _elements[key] = null;
        else
            _elements[key] = type;
        if (element.ElementSchemaType is { } schemaType)
            Visit(schemaType, seen);
    }

    private void Visit(XmlSchemaType type, HashSet<XmlSchemaType> seen)
    {
        if (type is XmlSchemaComplexType complex && seen.Add(type))
            Visit(complex.ContentTypeParticle, seen);
    }

    private void Visit(XmlSchemaParticle? particle, HashSet<XmlSchemaType> seen)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
                Visit(element, seen);
                break;
            case XmlSchemaGroupBase group:
                foreach (var item in group.Items)
                    Visit(item as XmlSchemaParticle, seen);
                break;
        }
    }

    /// <summary>A simple type, or a complex type with simple content, by its primitive type; null for element-only or mixed content.</summary>
    private static XQueryType? TypeOf(XmlSchemaType? type) => type?.Datatype?.TypeCode switch
    {
        null => null,
        XmlTypeCode.Decimal => XQueryType.Decimal,
        XmlTypeCode.Integer or XmlTypeCode.NonPositiveInteger or XmlTypeCode.NegativeInteger or XmlTypeCode.Long
            or XmlTypeCode.Int or XmlTypeCode.Short or XmlTypeCode.Byte or XmlTypeCode.NonNegativeInteger
            or XmlTypeCode.UnsignedLong or XmlTypeCode.UnsignedInt or XmlTypeCode.UnsignedShort or XmlTypeCode.UnsignedByte
            or XmlTypeCode.PositiveInteger => XQueryType.Integer,
        XmlTypeCode.Double => XQueryType.Double,
        XmlTypeCode.Boolean => XQueryType.Boolean,
        XmlTypeCode.String or XmlTypeCode.NormalizedString or XmlTypeCode.Token or XmlTypeCode.Language
            or XmlTypeCode.NmToken or XmlTypeCode.Name or XmlTypeCode.NCName or XmlTypeCode.Id or XmlTypeCode.Idref
            or XmlTypeCode.Entity or XmlTypeCode.AnyUri => XQueryType.String,
        XmlTypeCode.AnyAtomicType or XmlTypeCode.UntypedAtomic => XQueryType.Untyped,
        _ => XQueryType.Other,
    };
}
