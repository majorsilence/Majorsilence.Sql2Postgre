using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// FOR XML RAW, AUTO (over one table) and PATH as real XML: one element per row
/// (<c>xmlelement</c>, with <c>xmlattributes</c> and <c>xmlforest</c>), the rows joined with
/// <c>xmlagg</c> in the query's order. RAW and AUTO write columns as attributes, or as elements
/// with ELEMENTS; PATH writes elements, <c>@name</c> columns as attributes and <c>text()</c> or
/// unnamed columns as text. NULL columns are left out in both engines. ROOT wraps the rows; without
/// TYPE the result is text. A bit is 1 or 0, as SQL Server writes it.
/// </summary>
public sealed partial class QueryTranslator
{
    /// <summary>The name SQL Server gives a top-level FOR XML result's one column.</summary>
    public const string ForXmlColumn = "XML_F52E2B61-18A1-11d1-B105-00805F49916B";

    private PgQuery ForXmlElements(QuerySpecification spec, IList<XmlForClauseOption> options, ExpressionContext c)
    {
        var mode = options.FirstOrDefault(o => o.OptionKind is XmlForClauseOptions.Raw or XmlForClauseOptions.Auto or XmlForClauseOptions.Path or XmlForClauseOptions.Explicit)
                   ?? throw ExpressionTranslator.Untranslatable(spec, "FOR XML without a mode", DiagnosticCodes.ForXmlUnsupported);
        if (mode.OptionKind == XmlForClauseOptions.Explicit
            || options.Any(o => o.OptionKind is XmlForClauseOptions.ElementsXsiNil or XmlForClauseOptions.XmlData or XmlForClauseOptions.XmlSchema))
            throw ExpressionTranslator.Untranslatable(spec, $"FOR XML {mode.OptionKind.ToString().ToUpperInvariant()} with these options", DiagnosticCodes.ForXmlUnsupported);
        bool elements = options.Any(o => o.OptionKind is XmlForClauseOptions.Elements or XmlForClauseOptions.ElementsAbsent);
        bool type = options.Any(o => o.OptionKind == XmlForClauseOptions.Type);
        string? root = options.FirstOrDefault(o => o.OptionKind == XmlForClauseOptions.Root) is { } r ? (r.Value as StringLiteral)?.Value ?? "root" : null;

        string? rowName = mode.OptionKind switch
        {
            XmlForClauseOptions.Raw => (mode.Value as StringLiteral)?.Value ?? "row",
            XmlForClauseOptions.Path => mode.Value is StringLiteral { Value: var p } ? (p.Length == 0 ? null : p) : "row",
            _ => AutoElementName(spec),
        };
        bool path = mode.OptionKind == XmlForClauseOptions.Path;

        var inner = Specification(spec, c);
        if (inner.Targets.Any(t => t.Star))
            throw ExpressionTranslator.Untranslatable(spec, "FOR XML over SELECT *", DiagnosticCodes.ForXmlUnsupported);
        var columns = _model.OutputColumns(spec, c.Scope, c.Schema);
        string Output(int i) => $"xml_value_{i + 1}";
        var orderKeys = inner.OrderBy;
        var rows = inner with
        {
            Targets =
            [
                .. inner.Targets.Select((t, i) => columns.ElementAtOrDefault(i)?.Type?.Name == "bit"
                    ? new PgTarget(new PgCast(t.Expression!, "integer"), Output(i))
                    : t with { Alias = Output(i) }),
                .. orderKeys.Select((k, i) => new PgTarget(k.Expression, $"sort_key_{i + 1}")),
            ],
            OrderBy = spec.TopRowFilter is null ? [] : inner.OrderBy,
        };

        // One row's XML: attributes first (xmlelement wants them there), then content in column order.
        var attributes = new List<string>();
        var content = new List<string>();
        var args = new List<PgExpr>();
        for (int i = 0; i < inner.Targets.Count; i++)
        {
            string name = columns.ElementAtOrDefault(i)?.Name ?? "";
            string arg = $"{{{args.Count}}}";
            args.Add(new PgColumnRef(Output(i)));
            if (path && (name.Length == 0 || name is "text()" or "data()"))
                content.Add($"CAST({arg} AS text)");
            else if (path && name.StartsWith('@'))
                attributes.Add($"{arg} AS {XmlName(name[1..], spec)}");
            else if (name.Length == 0)
                throw ExpressionTranslator.Untranslatable(spec, $"FOR XML {mode.OptionKind.ToString().ToUpperInvariant()} with an unnamed column", DiagnosticCodes.ForXmlUnsupported);
            else if (!path && !elements)
                attributes.Add($"{arg} AS {XmlName(name, spec)}");
            else
                content.Add($"xmlforest({arg} AS {XmlName(name, spec)})");
        }
        string row;
        if (rowName is null)
        {
            if (attributes.Count > 0)
                throw ExpressionTranslator.Untranslatable(spec, "FOR XML PATH('') with attribute columns", DiagnosticCodes.ForXmlUnsupported);
            row = content.Count == 1 ? content[0] : $"xmlconcat({string.Join(", ", content)})";
        }
        else
        {
            var parts = new List<string>();
            if (attributes.Count > 0)
                parts.Add($"xmlattributes({string.Join(", ", attributes)})");
            parts.AddRange(content);
            row = $"xmlelement(name {XmlName(rowName, spec)}{string.Concat(parts.Select(p => ", " + p))})";
        }

        var aggregate = new PgFunctionCall("xmlagg", [new PgTemplate(row, args)])
        {
            OrderBy = orderKeys.Select((k, i) => k with { Expression = new PgColumnRef($"sort_key_{i + 1}") }).ToList(),
        };
        PgQuery xml = new PgSelect { Targets = [new PgTarget(aggregate, "xml_rows")], From = [new PgSubqueryRef(rows, "for_xml_rows", [])] };
        PgExpr result = new PgColumnRef("xml_rows");
        if (root is not null)
            // No rows give no XML at all, not an empty root element.
            result = new PgCase(null, [new PgWhen(new PgIsNull(result, Negated: true), new PgTemplate($"xmlelement(name {XmlName(root, spec)}, {{0}})", [result]))], null);
        return new PgSelect
        {
            Targets = [new PgTarget(type ? result : new PgCast(result, "text"), ForXmlColumn)],
            From = [new PgSubqueryRef(xml, "for_xml", [])],
        };
    }

    /// <summary>FOR XML AUTO over one table: its alias, or its name as written.</summary>
    private static string AutoElementName(QuerySpecification spec) => spec.FromClause?.TableReferences is [NamedTableReference n]
        ? n.Alias?.Value ?? string.Join('.', n.SchemaObject.Identifiers.Select(i => i.Value))
        : throw ExpressionTranslator.Untranslatable(spec, "FOR XML AUTO over a join (it nests the tables' elements)", DiagnosticCodes.ForXmlUnsupported);

    /// <summary>An XML name, quoted as an identifier so its case is kept; a path (<c>a/b</c>) is not translated.</summary>
    private static string XmlName(string name, TSqlFragment source)
    {
        if (name.Contains('/'))
            throw ExpressionTranslator.Untranslatable(source, $"FOR XML PATH with a nested name ({name})", DiagnosticCodes.ForXmlUnsupported);
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}
