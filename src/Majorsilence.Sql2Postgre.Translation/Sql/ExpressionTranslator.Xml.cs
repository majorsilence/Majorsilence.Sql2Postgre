using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// The xml methods, over PostgreSQL's XMLTABLE with the XQuery turned into XPath 1.0
/// (<see cref="XQueryPath"/>). An xml column or variable is a document, read from <c>'/'</c>; a
/// row of <c>nodes()</c> is an element, read from <c>'/*'</c>, so relative paths start at it as in
/// SQL Server:
/// <list type="bullet">
/// <item><c>x.value(path, type)</c>: the column <c>v type PATH path</c> (the string value of the
/// node, converted; NULL when there is none).</item>
/// <item><c>x.exist(path)</c>: <c>boolean(path)</c>, as a bit.</item>
/// <item><c>x.query(path)</c>: an xml column, the nodes concatenated (empty when there are none).</item>
/// <item><c>x.nodes(path)</c> in FROM: XMLTABLE with one xml column per row (<see cref="XmlNodes"/>).</item>
/// <item><c>x.modify(…)</c> outside <c>SET</c>: S2P3040 (in <c>SET</c>, <see cref="XmlModify"/>).</item>
/// </list>
/// </summary>
public sealed partial class ExpressionTranslator
{
    private PgExpr XmlMethod(FunctionCall f, XmlTarget target, ExpressionContext c)
    {
        string method = f.FunctionName.Value.ToLowerInvariant();
        if (method == "modify")
            throw Untranslatable(f, "the xml modify() method (PostgreSQL's xml type has no update functions: build the new document instead)", DiagnosticCodes.XmlModify);
        if (f.Parameters.Count == 0 || f.Parameters[0] is not StringLiteral pathLiteral)
            throw Untranslatable(f, $"xml {method}() with a path that is not a literal", DiagnosticCodes.XQueryUnsupported);
        var xpath = XPath(pathLiteral.Value, f);
        var document = Scalar(target.Expression, c);
        string rowPath = target.IsNode ? "/*" : "/";
        PgXmlTableColumn column;
        switch (method)
        {
            case "value":
                if (f.Parameters.Count != 2 || f.Parameters[1] is not StringLiteral typeLiteral || SemanticModel.ParseDataType(typeLiteral.Value) is not { } dataType)
                    throw Untranslatable(f, "xml value() without a literal type");
                var type = model.TypeOf(dataType, c.Schema);
                var mapped = types.Map(type with { UserTypeName = null }, c.Owner);
                if (mapped.Extension is not null)
                    c.Extensions.Add(mapped.Extension);
                column = new PgXmlTableColumn("v", mapped.PgType, xpath.Path);
                break;
            case "exist":
                column = new PgXmlTableColumn("v", "boolean", $"boolean({xpath.Path})");
                break;
            case "query":
                column = new PgXmlTableColumn("v", "xml", xpath.Path, new PgCast(PgLiteral.String(""), "xml"));
                break;
            default:
                throw MethodCall(f);
        }
        // SQL Server cuts a longer string to the length value() asks for; an XMLTABLE column of
        // that length would fail, so the string is read whole and cast, which cuts it the same way.
        string? cutTo = null;
        if (method == "value" && (column.Type.StartsWith("varchar(", StringComparison.Ordinal) || column.Type.StartsWith("char(", StringComparison.Ordinal)))
        {
            cutTo = column.Type;
            column = column with { Type = "text" };
        }
        PgExpr result = new PgSubquery(new PgSelect
        {
            Targets = [new PgTarget(new PgColumnRef("v"))],
            From = [new PgXmlTable(xpath.Namespaces, rowPath, document, [column])],
        });
        return cutTo is null ? result : new PgCast(result, cutTo);
    }

    /// <summary>
    /// <c>CROSS APPLY x.nodes(path) AS t(c)</c>: one row per node, its column <c>c</c> the node as
    /// xml. Under a <c>nodes()</c> row a relative path starts at that element (<c>/*/</c>).
    /// </summary>
    public PgFromItem XmlNodes(SchemaObjectFunctionTableReference f, ExpressionContext c)
    {
        var name = f.SchemaObject;
        var targetParts = name.Identifiers.Take(name.Identifiers.Count - 1).ToList();
        var target = new ColumnReferenceExpression { MultiPartIdentifier = new MultiPartIdentifier() };
        foreach (var part in targetParts)
            target.MultiPartIdentifier.Identifiers.Add(part);
        var resolved = c.Scope.ResolveColumn(targetParts.Select(p => p.Value).ToList());
        if (resolved?.Column.Type?.Name is not (null or "xml"))
            throw Untranslatable(f, "nodes() on a column that is not xml");
        if (f.Parameters.Count != 1 || f.Parameters[0] is not StringLiteral pathLiteral)
            throw Untranslatable(f, "xml nodes() with a path that is not a literal", DiagnosticCodes.XQueryUnsupported);
        var xpath = XPath(pathLiteral.Value, f);
        string path = xpath.Path;
        if (resolved?.Binding.Relation.XmlNodes == true && !path.StartsWith('/'))
        {
            path = path.StartsWith("./", StringComparison.Ordinal) ? "/*" + path[1..]
                : char.IsLetter(path[0]) || path[0] is '@' or '*' or '_' ? "/*/" + path
                : throw Untranslatable(f, "a relative nodes() path that does not start with a step", DiagnosticCodes.XQueryUnsupported);
        }
        string column = names.Alias(f.Columns.Count > 0 ? f.Columns[0].Value : "c");
        return new PgXmlTable(xpath.Namespaces, path, Scalar(target, c), [new PgXmlTableColumn(column, "xml", ".")],
            names.Alias(f.Alias?.Value ?? "nodes"));
    }

    private static XPathExpression XPath(string xquery, TSqlFragment f)
    {
        try
        {
            return XQueryPath.Convert(xquery, []);
        }
        catch (XQueryNotXPathException ex)
        {
            throw Untranslatable(f, $"an xml method whose path uses {ex.What} (PostgreSQL evaluates XPath 1.0)", DiagnosticCodes.XQueryUnsupported);
        }
    }
}
