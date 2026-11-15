using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>FOR XML PATH('') string building and FOR JSON, as aggregates over the query's rows.</summary>
public sealed partial class QueryTranslator
{
    /// <summary>
    /// <c>SELECT ',' + x FROM … FOR XML PATH('')</c> concatenates the rows' values: string_agg over
    /// the query, in the query's order. Without TYPE, SQL Server returns the text XML-escaped
    /// (&amp;, &lt;, &gt;); <c>(… FOR XML PATH(''), TYPE).value('.', …)</c> returns it as it is.
    /// Any other FOR XML builds real XML, which is not translated.
    /// </summary>
    private PgQuery ForXmlQuery(QuerySpecification spec, ExpressionContext c)
    {
        var options = ((XmlForClause)spec.ForClause).Options;
        bool isPathEmpty = options.Any(o => o.OptionKind == XmlForClauseOptions.Path && (o.Value is null || o.Value is StringLiteral { Value: "" }));
        bool type = options.Any(o => o.OptionKind == XmlForClauseOptions.Type);
        if (!isPathEmpty || options.Any(o => o.OptionKind is not (XmlForClauseOptions.Path or XmlForClauseOptions.Type)))
            throw ExpressionTranslator.Untranslatable(spec, "FOR XML other than PATH('') concatenation", DiagnosticCodes.ForXmlUnsupported);
        return new PgSelect { Targets = [new PgTarget(ConcatenatedRows(spec, c, entitize: !type))] };
    }

    /// <summary>The <c>(…, TYPE).value('.', …)</c> form: the concatenation without XML escaping.</summary>
    private PgExpr ForXmlPath(QueryExpression q, ExpressionContext c, bool entitize)
    {
        if (q is not QuerySpecification { ForClause: XmlForClause } spec)
            throw ExpressionTranslator.Untranslatable(q, ".value() on something other than FOR XML PATH('')");
        return new PgSubquery(new PgSelect { Targets = [new PgTarget(ConcatenatedRows(spec, c, entitize))] });
    }

    private PgExpr ConcatenatedRows(QuerySpecification spec, ExpressionContext c, bool entitize)
    {
        var elements = spec.SelectElements.Cast<SelectElement>().ToList();
        if (elements.Any(e => e is not SelectScalarExpression { ColumnName: null } and not SelectScalarExpression { ColumnName.Value: "text()" or "data()" }))
            throw ExpressionTranslator.Untranslatable(spec, "FOR XML PATH('') with named columns (they become XML elements)", DiagnosticCodes.ForXmlUnsupported);

        // The rows, each as one text value plus the keys they are ordered by, in a derived table;
        // string_agg over it in that order. This keeps DISTINCT, GROUP BY and TOP as the query had them.
        var inner = Specification(spec, c);
        var scope = _model.QueryScope(spec, c.Scope, c.Schema);
        var ic = c.With(scope);
        PgExpr value = elements.Cast<SelectScalarExpression>()
            .Select(e => (PgExpr)new PgFunctionCall("COALESCE", [new PgCast(_x.Scalar(e.Expression, ic), "text"), PgLiteral.String("")]))
            .Aggregate((l, r) => new PgBinary("||", l, r));
        var orderKeys = inner.OrderBy;
        var rows = inner with
        {
            Targets = [new PgTarget(value, "concatenated_value"), .. orderKeys.Select((k, i) => new PgTarget(k.Expression, $"sort_key_{i + 1}"))],
            OrderBy = spec.TopRowFilter is null ? [] : inner.OrderBy,
        };
        var aggregate = new PgFunctionCall("string_agg", [new PgColumnRef("concatenated_value"), PgLiteral.String("")])
        {
            OrderBy = orderKeys.Select((k, i) => k with { Expression = new PgColumnRef($"sort_key_{i + 1}") }).ToList(),
        };
        PgExpr result = new PgSubquery(new PgSelect { Targets = [new PgTarget(aggregate)], From = [new PgSubqueryRef(rows, "for_xml_rows", [])] });
        if (entitize)
            foreach (var (from, to) in new[] { ("&", "&amp;"), ("<", "&lt;"), (">", "&gt;") })
                result = new PgFunctionCall("replace", [result, PgLiteral.String(from), PgLiteral.String(to)]);
        return result;
    }

    /// <summary>
    /// FOR JSON PATH (and AUTO over one table): the rows as an array of objects, built with
    /// json_build_object in the query's order. Dotted names nest objects; NULL properties are left
    /// out unless INCLUDE_NULL_VALUES; ROOT('x') wraps the array; WITHOUT_ARRAY_WRAPPER joins the
    /// objects with commas.
    /// </summary>
    private PgQuery ForJson(QuerySpecification spec, JsonForClause json, ExpressionContext c)
    {
        var options = json.Options;
        bool auto = options.Any(o => o.OptionKind == JsonForClauseOptions.Auto);
        if (auto && (spec.FromClause?.TableReferences.Count > 1 || spec.FromClause?.TableReferences.FirstOrDefault() is JoinTableReference))
            throw ExpressionTranslator.Untranslatable(spec, "FOR JSON AUTO over more than one table (it nests by table)", DiagnosticCodes.ForJsonUnsupported);
        bool includeNulls = options.Any(o => o.OptionKind == JsonForClauseOptions.IncludeNullValues);
        bool noArray = options.Any(o => o.OptionKind == JsonForClauseOptions.WithoutArrayWrapper);
        string? root = options.FirstOrDefault(o => o.OptionKind == JsonForClauseOptions.Root) is { } r
            ? (r.Value as StringLiteral)?.Value ?? "root" : null;

        var jsonContext = c.WithInsideJson();
        var inner = Specification(spec, jsonContext);
        var columns = _model.OutputColumns(spec, c.Scope, c.Schema);
        var names = columns.Select((col, i) => string.IsNullOrEmpty(col.Name) ? throw ExpressionTranslator.Untranslatable(spec, "FOR JSON with an unnamed column", DiagnosticCodes.ForJsonUnsupported) : col.Name).ToList();
        string Output(int i) => $"json_value_{i + 1}";
        var orderKeys = inner.OrderBy;
        var rows = inner with
        {
            Targets = [.. inner.Targets.Select((t, i) => t with { Alias = Output(i) }), .. orderKeys.Select((k, i) => new PgTarget(k.Expression, $"sort_key_{i + 1}"))],
            OrderBy = spec.TopRowFilter is null ? [] : inner.OrderBy,
        };
        if (inner.Targets.Any(t => t.Star))
            throw ExpressionTranslator.Untranslatable(spec, "FOR JSON over SELECT *", DiagnosticCodes.ForJsonUnsupported);

        PgExpr obj = JsonObject(names.Select((n, i) => (Path: n.Split('.'), Value: (PgExpr)new PgColumnRef(Output(i)))).ToList());
        if (!includeNulls)
            obj = new PgFunctionCall("json_strip_nulls", [obj]);
        var sorted = orderKeys.Select((k, i) => k with { Expression = new PgColumnRef($"sort_key_{i + 1}") }).ToList();
        PgExpr aggregate = noArray
            ? new PgFunctionCall("string_agg", [new PgCast(obj, "text"), PgLiteral.String(",")]) { OrderBy = sorted }
            : new PgFunctionCall("json_agg", [obj]) { OrderBy = sorted };
        if (root is not null && !noArray)
            aggregate = new PgFunctionCall("json_build_object", [PgLiteral.String(root), aggregate]);
        // SQL Server names the result column JSON_F52E2B61-18A1-11d1-B105-00805F49916B; so does this.
        return new PgSelect
        {
            Targets = [new PgTarget(new PgCast(aggregate, "text"), ForJsonColumn)],
            From = [new PgSubqueryRef(rows, "for_json_rows", [])],
        };
    }

    public const string ForJsonColumn = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>json_build_object over (path, value) pairs, nesting by the dotted path.</summary>
    private static PgExpr JsonObject(IReadOnlyList<(string[] Path, PgExpr Value)> members)
    {
        var args = new List<PgExpr>();
        foreach (var group in members.GroupBy(m => m.Path[0]))
        {
            args.Add(PgLiteral.String(group.Key));
            var nested = group.Where(m => m.Path.Length > 1).Select(m => (m.Path[1..], m.Value)).ToList();
            args.Add(nested.Count > 0 ? JsonObject(nested) : group.First().Value);
        }
        return new PgFunctionCall("json_build_object", args);
    }
}
