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
        var levels = auto ? AutoLevels(spec, c) : null;
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

        if (levels is { Count: > 1 })
            return AutoNested(rows, inner, levels, names, includeNulls, noArray, root);

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

    /// <summary>One level of FOR JSON AUTO's nesting: a table (its alias as SQL Server names the nested array) and the select-list positions that belong to it.</summary>
    private sealed record AutoLevel(string Key, List<int> Columns);

    /// <summary>
    /// FOR JSON AUTO's levels, as SQL Server forms them: one per table, in the order its first
    /// column appears in the select list. A column belongs to its table's level wherever it stands;
    /// an expression that is not a column belongs to the level of the column before it.
    /// </summary>
    private List<AutoLevel> AutoLevels(QuerySpecification spec, ExpressionContext c)
    {
        var scope = _model.QueryScope(spec, c.Scope, c.Schema);
        var finder = new UnaliasedTables();
        spec.FromClause?.Accept(finder);
        var unaliased = finder.Tables;
        var levels = new List<AutoLevel>();
        AutoLevel? current = null;
        for (int i = 0; i < spec.SelectElements.Count; i++)
        {
            if (spec.SelectElements[i] is SelectScalarExpression { Expression: ColumnReferenceExpression col }
                && scope.ResolveColumn(col.MultiPartIdentifier.Identifiers.Select(x => x.Value).ToList()) is { Depth: 0 } resolved)
            {
                // A table without an alias is named as written (dbo.City), as SQL Server keys it.
                string key = unaliased.FirstOrDefault(n => n.SchemaObject.BaseIdentifier.Value.Equals(resolved.Binding.Alias, StringComparison.OrdinalIgnoreCase)) is { } named
                    ? string.Join('.', named.SchemaObject.Identifiers.Select(x => x.Value))
                    : resolved.Binding.Alias;
                current = levels.FirstOrDefault(l => l.Key == key);
                if (current is null)
                    levels.Add(current = new AutoLevel(key, []));
            }
            if (current is null)
                levels.Add(current = new AutoLevel("", []));
            current.Columns.Add(i);
        }
        // Expressions before the first column join the first table's level.
        if (levels.Count > 1 && levels[0].Key == "")
        {
            levels[1].Columns.InsertRange(0, levels[0].Columns);
            levels.RemoveAt(0);
        }
        return levels;
    }

    private sealed class UnaliasedTables : TSqlFragmentVisitor
    {
        public List<NamedTableReference> Tables { get; } = [];

        public override void Visit(NamedTableReference node)
        {
            if (node.Alias is null)
                Tables.Add(node);
        }

        // A subquery's tables are not this query's.
        public override void ExplicitVisit(QueryDerivedTable node) { }
    }

    /// <summary>
    /// FOR JSON AUTO over joined tables: each level's objects hold its own columns and, under the
    /// next table's alias, an array of that level's objects. Rows group into a parent object while
    /// the parent's columns stay the same from one row to the next (in the query's order), as
    /// SQL Server groups them; the deepest level has one object per row. Built in stages over the
    /// query's rows: a row number, a break flag per level (any of its or its parents' columns
    /// differs from the row before), their running sums as group numbers, then one aggregation
    /// per level from the deepest up.
    /// </summary>
    private static PgQuery AutoNested(PgSelect rows, PgSelect inner, List<AutoLevel> levels, List<string> names, bool includeNulls, bool noArray, string? root)
    {
        static string Output(int i) => $"json_value_{i + 1}";
        static PgExpr Col(string name) => new PgColumnRef(name);
        var byRow = new PgWindow([], [new PgSortKey(Col("rn"), false)]);
        var numbered = rows with
        {
            Targets = [.. rows.Targets.Where(t => t.Alias?.StartsWith("json_value_", StringComparison.Ordinal) == true),
                new PgTarget(new PgFunctionCall("row_number", []) { Over = new PgWindow([], inner.OrderBy) }, "rn")],
        };
        int k = levels.Count;
        var allOutputs = Enumerable.Range(0, names.Count).Select(Output).ToList();

        // A break at level j: this row's columns of levels 1..j differ from the row before (as text, which compares any type).
        var flagTargets = allOutputs.Select(o => new PgTarget(Col(o))).Append(new PgTarget(Col("rn"))).ToList();
        for (int j = 0; j < k - 1; j++)
        {
            var cols = levels.Take(j + 1).SelectMany(l => l.Columns).Select(i => (PgExpr)new PgCast(Col(Output(i)), "text")).ToList();
            var current = new PgFunctionCall("ROW", cols);
            var previous = new PgFunctionCall("ROW", cols.Select(x => (PgExpr)new PgFunctionCall("lag", [x]) { Over = byRow }).ToList());
            flagTargets.Add(new PgTarget(new PgCase(null,
                [new PgWhen(new PgTemplate("{0} > 1 AND {1} IS NOT DISTINCT FROM {2}", [Col("rn"), current, previous]), new PgLiteral("0"))],
                new PgLiteral("1")), $"break_{j + 1}"));
        }
        var flagged = new PgSelect { Targets = flagTargets, From = [new PgSubqueryRef(numbered, "json_rows", [])] };
        var groupTargets = allOutputs.Select(o => new PgTarget(Col(o))).Append(new PgTarget(Col("rn"))).ToList();
        for (int j = 0; j < k - 1; j++)
            groupTargets.Add(new PgTarget(new PgFunctionCall("sum", [Col($"break_{j + 1}")]) { Over = byRow }, $"group_{j + 1}"));
        PgQuery current_ = new PgSelect { Targets = groupTargets, From = [new PgSubqueryRef(flagged, "json_breaks", [])] };

        PgExpr Strip(PgExpr obj) => includeNulls ? obj : new PgFunctionCall("json_strip_nulls", [obj]);
        // The deepest level: one object per row, with the parents' columns and groups carried up.
        var deepest = levels[k - 1];
        var targets = Enumerable.Range(1, k - 1).Select(j => new PgTarget(Col($"group_{j}"))).ToList();
        targets.Add(new PgTarget(Col("rn"), "first_rn"));
        targets.AddRange(levels.Take(k - 1).SelectMany(l => l.Columns).Select(i => new PgTarget(Col(Output(i)))));
        targets.Add(new PgTarget(Strip(JsonObject(deepest.Columns.Select(i => (Path: new[] { names[i] }, Value: Col(Output(i)))).ToList())), "obj"));
        current_ = new PgSelect { Targets = targets, From = [new PgSubqueryRef(current_, "json_groups", [])] };

        var byFirst = new List<PgSortKey> { new(Col("first_rn"), false) };
        for (int j = k - 1; j >= 1; j--)
        {
            var level = levels[j - 1];
            PgExpr First(string column) => new PgTemplate("({0})[1]", [new PgFunctionCall("array_agg", [Col(column)]) { OrderBy = byFirst }]);
            var members = level.Columns.Select(i => (Path: new[] { names[i] }, Value: First(Output(i)))).ToList();
            members.Add((Path: new[] { levels[j].Key }, Value: new PgFunctionCall("json_agg", [Col("obj")]) { OrderBy = byFirst }));
            var levelTargets = Enumerable.Range(1, j - 1).Select(g => new PgTarget(Col($"group_{g}"))).ToList();
            levelTargets.Add(new PgTarget(new PgFunctionCall("min", [Col("first_rn")]), "first_rn"));
            levelTargets.AddRange(levels.Take(j - 1).SelectMany(l => l.Columns).Select(i => new PgTarget(First(Output(i)), Output(i))));
            levelTargets.Add(new PgTarget(Strip(JsonObject(members)), "obj"));
            current_ = new PgSelect
            {
                Targets = levelTargets,
                From = [new PgSubqueryRef(current_, $"json_level_{j + 1}", [])],
                GroupBy = Enumerable.Range(1, j).Select(g => (PgGroupItem)new PgGroupExpr(Col($"group_{g}"))).ToList(),
            };
        }

        PgExpr aggregate = noArray
            ? new PgFunctionCall("string_agg", [new PgCast(Col("obj"), "text"), PgLiteral.String(",")]) { OrderBy = byFirst }
            : new PgFunctionCall("json_agg", [Col("obj")]) { OrderBy = byFirst };
        if (root is not null && !noArray)
            aggregate = new PgFunctionCall("json_build_object", [PgLiteral.String(root), aggregate]);
        return new PgSelect
        {
            Targets = [new PgTarget(new PgCast(aggregate, "text"), ForJsonColumn)],
            From = [new PgSubqueryRef(current_, "json_level_1", [])],
        };
    }

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
