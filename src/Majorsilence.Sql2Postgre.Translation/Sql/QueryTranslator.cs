using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Translates T-SQL queries — SELECT with its TOP, APPLY, CTEs, set operations, GROUP BY
/// variants, PIVOT and UNPIVOT, FOR XML PATH concatenation and FOR JSON — to PostgreSQL, using
/// the <see cref="ExpressionTranslator"/> for every expression and the semantic model for every
/// name. DML (INSERT, UPDATE, DELETE, MERGE, SELECT INTO) is in the partial file next to this one.
/// </summary>
public sealed partial class QueryTranslator
{
    private readonly ExpressionTranslator _x;
    private readonly SemanticModel _model;
    private readonly NameMapper _names;

    public QueryTranslator(ExpressionTranslator expressions)
    {
        _x = expressions;
        _model = expressions.Model;
        _names = expressions.Names;
        _x.Subquery = (q, c) => new PgSubquery(Query(q, c));
        _x.SubqueryPredicates = SubqueryPredicate;
        _x.XmlValueSubquery = (q, c) => ForXmlPath(q, c, entitize: false);
    }

    public ExpressionTranslator Expressions => _x;

    // ---- queries ----

    /// <summary>A query expression, with an optional WITH clause, in the context's scope (its outer scope for correlation).</summary>
    public PgQuery Query(QueryExpression q, ExpressionContext c, WithCtesAndXmlNamespaces? with = null)
    {
        var scope = c.Scope;
        IReadOnlyList<PgCte> ctes = [];
        bool recursive = false;
        if (with is { CommonTableExpressions.Count: > 0 })
        {
            scope = c.Scope.Child();
            _model.DefineCtes(with, scope, c.Schema);
            var cteContext = c.With(scope);
            ctes = with.CommonTableExpressions.Select(cte => new PgCte(_names.Alias(cte.ExpressionName.Value),
                cte.Columns.Select(col => _names.Alias(col.Value)).ToList(), Query(cte.QueryExpression, cteContext))).ToList();
            recursive = with.CommonTableExpressions.Any(cte => ReferencesName(cte.QueryExpression, cte.ExpressionName.Value));
        }
        var translated = Body(q, c.With(scope));
        return translated switch
        {
            PgSelect s when ctes.Count > 0 => s with { With = ctes, Recursive = recursive },
            PgSetOperation o when ctes.Count > 0 => o with { With = ctes, Recursive = recursive },
            _ when ctes.Count > 0 => new PgSelect { With = ctes, Recursive = recursive, Targets = [PgTarget.All()], From = [new PgSubqueryRef(translated, "q", [])] },
            _ => translated,
        };
    }

    private PgQuery Body(QueryExpression q, ExpressionContext c)
    {
        switch (q)
        {
            case QueryParenthesisExpression p when p.OrderByClause is null && p.OffsetClause is null:
                return Body(p.QueryExpression, c);
            case QuerySpecification spec when spec.ForClause is XmlForClause:
                return ForXmlQuery(spec, c);
            case QuerySpecification spec when spec.ForClause is JsonForClause json:
                return ForJson(spec, json, c);
            case QuerySpecification spec:
                return Specification(spec, c);
            case BinaryQueryExpression b:
                string op = b.BinaryQueryExpressionType switch
                {
                    BinaryQueryExpressionType.Union => b.All ? "UNION ALL" : "UNION",
                    BinaryQueryExpressionType.Except => "EXCEPT",
                    _ => "INTERSECT",
                };
                var set = new PgSetOperation(op, Body(b.FirstQueryExpression, c), Body(b.SecondQueryExpression, c));
                var outputs = _model.OutputColumns(b, c.Scope, c.Schema);
                var orderScope = c.Scope.Child();
                orderScope.Bind(new Binding("", new RelationInfo(null, ObjectKind.Table, outputs)));
                return set with
                {
                    OrderBy = OrderBy(b.OrderByClause, c.With(orderScope), outputs.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                    Offset = b.OffsetClause is null ? null : _x.Scalar(b.OffsetClause.OffsetExpression, c),
                    Limit = b.OffsetClause?.FetchExpression is null ? null : _x.Scalar(b.OffsetClause.FetchExpression, c),
                };
            default:
                throw ExpressionTranslator.Untranslatable(q, "this kind of query");
        }
    }

    private PgSelect Specification(QuerySpecification spec, ExpressionContext outer)
    {
        var scope = _model.QueryScope(spec, outer.Scope, outer.Schema);
        var c = outer.With(scope);
        var aliases = spec.SelectElements.OfType<SelectScalarExpression>().Where(s => s.ColumnName is not null)
            .Select(s => s.ColumnName.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var select = new PgSelect
        {
            Distinct = spec.UniqueRowFilter == UniqueRowFilter.Distinct,
            Targets = spec.SelectElements.Select(e => Target(e, c)).ToList(),
            From = spec.FromClause?.TableReferences.Select(r => FromItem(r, c, outer)).ToList() ?? [],
            Where = spec.WhereClause?.SearchCondition is { } where ? _x.Boolean(where, c) : null,
            GroupBy = GroupBy(spec.GroupByClause, c),
            Having = spec.HavingClause?.SearchCondition is { } having ? _x.Boolean(having, c) : null,
            OrderBy = OrderBy(spec.OrderByClause, c, aliases),
            Offset = spec.OffsetClause is null ? null : _x.Scalar(spec.OffsetClause.OffsetExpression, c),
            Limit = spec.OffsetClause?.FetchExpression is null ? null : _x.Scalar(spec.OffsetClause.FetchExpression, c),
        };
        return spec.TopRowFilter is null ? select : Top(select, spec.TopRowFilter, c);
    }

    /// <summary>
    /// TOP n is LIMIT n; TOP n WITH TIES is FETCH FIRST n ROWS WITH TIES; TOP n PERCENT counts the
    /// rows first, so the query runs twice.
    /// </summary>
    private PgSelect Top(PgSelect select, TopRowFilter top, ExpressionContext c)
    {
        var n = _x.Scalar(top.Expression, c);
        if (top.Percent)
        {
            c.Report(DiagnosticCodes.TopPercent, Severity.Info, "TOP … PERCENT counts the rows first: the query runs twice.");
            var counted = new PgSelect
            {
                Targets = [new PgTarget(new PgFunctionCall("ceil", [new PgBinary("/", new PgBinary("*", new PgFunctionCall("count", []) { Star = true }, n), new PgLiteral("100.0"))]))],
                From = [new PgSubqueryRef(select with { OrderBy = [], Limit = null, Offset = null }, "counted", [])],
            };
            return select with { Limit = new PgSubquery(counted) };
        }
        if (top.WithTies)
            return select with { Limit = n, WithTies = true };
        return select with { Limit = n };
    }

    private PgTarget Target(SelectElement e, ExpressionContext c) => e switch
    {
        SelectScalarExpression s => new PgTarget(ForJsonValue(s.Expression, c), s.ColumnName is null ? null : _names.Alias(s.ColumnName.Value)),
        SelectStarExpression { Qualifier: null } => PgTarget.All(),
        SelectStarExpression star => PgTarget.All(StarQualifier(star.Qualifier, c)),
        SelectSetVariable v => throw ExpressionTranslator.Untranslatable(v, "assigning a variable in SELECT outside procedural code", DiagnosticCodes.ProceduralGlobal),
        _ => throw ExpressionTranslator.Untranslatable(e, "this select item"),
    };

    /// <summary>A nested FOR JSON subquery is JSON inside JSON, not a string: it is cast to json.</summary>
    private PgExpr ForJsonValue(ScalarExpression e, ExpressionContext c)
    {
        var translated = _x.Scalar(e, c);
        return e is ScalarSubquery { QueryExpression: QuerySpecification { ForClause: JsonForClause } } && c.InsideJson
            ? new PgCast(translated, "json")
            : translated;
    }

    private string StarQualifier(MultiPartIdentifier qualifier, ExpressionContext c)
    {
        var parts = qualifier.Identifiers.Select(i => i.Value).ToList();
        return c.Scope.ResolveQualifier(parts[^1], parts.Count > 1 ? parts[^2] : null) is { } b ? _x.AliasFor(b) : _names.Alias(parts[^1]);
    }

    private IReadOnlyList<PgSortKey> OrderBy(OrderByClause? orderBy, ExpressionContext c, IReadOnlySet<string> outputAliases) =>
        orderBy?.OrderByElements.Select(o =>
        {
            // A bare name that is a select-list alias sorts by that output column (SQL Server prefers the alias).
            if (o.Expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [var id] } && outputAliases.Contains(id.Value))
            {
                bool descending = o.SortOrder == SortOrder.Descending;
                return new PgSortKey(new PgColumnRef(_names.Alias(id.Value)), descending, NullsFirst: !descending);
            }
            return _x.SortKey(o, c);
        }).ToList() ?? [];

    private IReadOnlyList<PgGroupItem> GroupBy(GroupByClause? groupBy, ExpressionContext c)
    {
        if (groupBy is null)
            return [];
        if (groupBy.All)
            throw ExpressionTranslator.Untranslatable(groupBy, "GROUP BY ALL");
        var items = groupBy.GroupingSpecifications.Select(g => GroupItem(g, c)).ToList();
        return groupBy.GroupByOption switch
        {
            GroupByOption.Rollup => [new PgGroupSets("ROLLUP", items.Select(ExprsOf).ToList())],
            GroupByOption.Cube => [new PgGroupSets("CUBE", items.Select(ExprsOf).ToList())],
            _ => items,
        };
    }

    private PgGroupItem GroupItem(GroupingSpecification g, ExpressionContext c) => g switch
    {
        ExpressionGroupingSpecification e => new PgGroupExpr(_x.Scalar(e.Expression, c)),
        RollupGroupingSpecification r => new PgGroupSets("ROLLUP", r.Arguments.Select(a => ExprsOf(GroupItem(a, c))).ToList()),
        CubeGroupingSpecification cube => new PgGroupSets("CUBE", cube.Arguments.Select(a => ExprsOf(GroupItem(a, c))).ToList()),
        GroupingSetsGroupingSpecification s => new PgGroupSets("GROUPING SETS", s.Sets.Select(a => ExprsOf(GroupItem(a, c))).ToList()),
        CompositeGroupingSpecification comp => new PgGroupSets("", [comp.Items.SelectMany(i => ExprsOf(GroupItem(i, c))).ToList()]),
        GrandTotalGroupingSpecification => new PgGroupSets("", [[]]),
        _ => throw ExpressionTranslator.Untranslatable(g, "this GROUP BY item"),
    };

    private static IReadOnlyList<PgExpr> ExprsOf(PgGroupItem item) => item switch
    {
        PgGroupExpr e => [e.Expression],
        PgGroupSets { Kind: "", Sets: [var only] } => only,
        _ => throw new InvalidOperationException("Nested grouping sets are not flattened."),
    };

    // ---- FROM ----

    /// <param name="c">The query's own context (its FROM bindings), for ON conditions and APPLY.</param>
    /// <param name="outer">The context outside the query, for derived tables (which cannot see their siblings).</param>
    private PgFromItem FromItem(TableReference r, ExpressionContext c, ExpressionContext outer)
    {
        switch (r)
        {
            case QualifiedJoin j:
                string kind = j.QualifiedJoinType switch
                {
                    QualifiedJoinType.LeftOuter => "LEFT JOIN",
                    QualifiedJoinType.RightOuter => "RIGHT JOIN",
                    QualifiedJoinType.FullOuter => "FULL JOIN",
                    _ => "JOIN",
                };
                if (j.JoinHint != JoinHint.None)
                    c.Report(DiagnosticCodes.HintDropped, Severity.Info, $"The {j.JoinHint} join hint is dropped; PostgreSQL's planner chooses the join method.");
                return new PgJoin(kind, FromItem(j.FirstTableReference, c, outer), FromItem(j.SecondTableReference, c, outer), _x.Boolean(j.SearchCondition, c));
            case UnqualifiedJoin j:
                var left = FromItem(j.FirstTableReference, c, outer);
                switch (j.UnqualifiedJoinType)
                {
                    case UnqualifiedJoinType.CrossApply:
                        return new PgJoin("CROSS JOIN", left, Lateral(FromItem(j.SecondTableReference, c, c)), null);
                    case UnqualifiedJoinType.OuterApply:
                        return new PgJoin("LEFT JOIN", left, Lateral(FromItem(j.SecondTableReference, c, c)), PgLiteral.True);
                    default:
                        return new PgJoin("CROSS JOIN", left, FromItem(j.SecondTableReference, c, outer), null);
                }
            case JoinParenthesisTableReference p:
                return FromItem(p.Join, c, outer);
            case NamedTableReference n:
                return NamedTable(n, c);
            case QueryDerivedTable d:
                return new PgSubqueryRef(Query(d.QueryExpression, outer), _names.Alias(d.Alias?.Value ?? "derived"), d.Columns.Select(col => _names.Alias(col.Value)).ToList());
            case InlineDerivedTable v:
                return new PgSubqueryRef(new PgValues(v.RowValues.Select(row => (IReadOnlyList<PgExpr>)row.ColumnValues.Select(x => _x.Scalar(x, outer)).ToList()).ToList()),
                    _names.Alias(v.Alias?.Value ?? "v"), v.Columns.Select(col => _names.Alias(col.Value)).ToList());
            case SchemaObjectFunctionTableReference f:
                if (SemanticModel.IsXmlNodesCall(f.SchemaObject))
                    throw ExpressionTranslator.Untranslatable(f, "the xml nodes() method");
                var routine = _model.ResolveRoutine(f.SchemaObject, c.Schema) ?? throw ExpressionTranslator.Untranslatable(f, "a table-valued function not in this database");
                var arguments = _model.Routine(routine) is { } info ? _x.RoutineArguments(f.Parameters, info, c) : f.Parameters.Select(p => _x.Scalar(p, c)).ToList();
                return new PgFunctionRef(_names.Routine(routine).Qualified, arguments,
                    _names.Alias(f.Alias?.Value ?? f.SchemaObject.BaseIdentifier.Value), f.Columns.Select(col => _names.Alias(col.Value)).ToList());
            case GlobalFunctionTableReference g:
                return BuiltinTableFunction(g, c);
            case PivotedTableReference p:
                return Pivot(p, outer);
            case UnpivotedTableReference u:
                return Unpivot(u, outer);
            case VariableTableReference v when TableVariable?.Invoke(v.Variable.Name) is { } table:
                return new PgTableRef(new PgName("", table), v.Alias is null ? null : _names.Alias(v.Alias.Value));
            case VariableTableReference v:
                throw ExpressionTranslator.Untranslatable(v, "a table variable outside procedural code", DiagnosticCodes.ProceduralGlobal);
            default:
                throw ExpressionTranslator.Untranslatable(r, "this FROM item");
        }
    }

    private static PgFromItem Lateral(PgFromItem item) => item switch
    {
        PgSubqueryRef s => s with { Lateral = true },
        PgFunctionRef f => f with { Lateral = true },
        _ => item,
    };

    private PgFromItem NamedTable(NamedTableReference n, ExpressionContext c)
    {
        if (n.TableHints.Count > 0)
            c.Report(DiagnosticCodes.HintDropped, Severity.Info,
                $"Table hints ({string.Join(", ", n.TableHints.Select(h => h.HintKind))}) are dropped; PostgreSQL has no equivalent (NOLOCK's dirty reads do not exist there).");
        if (n.TemporalClause is not null)
            throw ExpressionTranslator.Untranslatable(n, "FOR SYSTEM_TIME (temporal tables)");
        string written = n.SchemaObject.BaseIdentifier.Value;
        string? alias = n.Alias is null ? null : _names.Alias(n.Alias.Value);
        if (n.SchemaObject.SchemaIdentifier is null && c.Scope.Cte(written) is not null)
            return new PgTableRef(new PgName("", _names.Alias(written)), alias);
        GlobalTemp(n, written);
        if (written.StartsWith('#'))
            return new PgTableRef(new PgName("", TempTable(written)), alias);
        if (_model.IsExternal(n.SchemaObject))
            throw ExpressionTranslator.Untranslatable(n, "a table in another database or on a linked server", DiagnosticCodes.ExternalReference);
        var name = _model.ResolveRelation(n.SchemaObject, c.Schema)!;
        if (_model.Relation(name) is null)
            c.Report(DiagnosticCodes.UnresolvedColumn, Severity.Warning, $"{name} is not a table or view this database has; it is written as {_names.Relation(name).Qualified}.");
        return new PgTableRef(_names.Relation(name), alias);
    }

    /// <summary>A temp table's PostgreSQL name: its name without <c>#</c>, styled.</summary>
    public string TempTable(string written) => _names.Alias(written.TrimStart('#'));

    /// <summary>
    /// The temporary table a table variable (<c>@t</c>) is kept in, or null when it is not one the
    /// procedural translator declared (set by it).
    /// </summary>
    public Func<string, string?>? TableVariable { get; set; }

    /// <summary>
    /// The rows of <c>INSERT … EXEC procedure</c> as a query, or null when the procedure's rows
    /// cannot be read as one (set by the procedural translator).
    /// </summary>
    public Func<ExecuteInsertSource, ExpressionContext, PgQuery?>? ExecuteSource { get; set; }

    /// <summary>STRING_SPLIT is string_to_table; GENERATE_SERIES is generate_series. Both return a column named value.</summary>
    private PgFromItem BuiltinTableFunction(GlobalFunctionTableReference g, ExpressionContext c)
    {
        string alias = _names.Alias(g.Alias?.Value ?? g.Name.Value);
        var args = g.Parameters.Select(p => _x.Scalar(p, c)).ToList();
        return g.Name.Value.ToUpperInvariant() switch
        {
            "STRING_SPLIT" when g.Parameters.Count == 2 => new PgFunctionRef("string_to_table", [_x.Text(g.Parameters[0], c), args[1]], alias, ["value"]),
            "GENERATE_SERIES" => new PgFunctionRef("generate_series", args, alias, ["value"]),
            _ => throw ExpressionTranslator.Untranslatable(g, $"the table function {g.Name.Value}"),
        };
    }

    /// <summary>
    /// PIVOT (agg(v) FOR col IN ([a], [b])) as a grouped query: every other source column is a
    /// group, and each IN value is agg(v) FILTER (WHERE col = 'a').
    /// </summary>
    private PgFromItem Pivot(PivotedTableReference p, ExpressionContext outer)
    {
        var inner = outer.Scope.Child();
        _model.BindTableReference(p.TableReference, inner, outer.Schema);
        var c = outer.With(inner);
        var source = FromItem(p.TableReference, c, outer);
        var binding = inner.Bindings.Single();
        string pivotColumn = p.PivotColumn.MultiPartIdentifier.Identifiers[^1].Value;
        var valueNames = p.ValueColumns.Select(v => v.MultiPartIdentifier.Identifiers[^1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = binding.Relation.Columns.Where(col => !string.Equals(col.Name, pivotColumn, StringComparison.OrdinalIgnoreCase) && !valueNames.Contains(col.Name)).ToList();
        var pivotType = binding.Relation.Column(pivotColumn)?.Type;
        string aggregate = p.AggregateFunctionIdentifier.Identifiers[^1].Value.ToLowerInvariant() switch
        {
            "count" => "count",
            "sum" => "sum",
            "avg" => "avg",
            "min" => "min",
            "max" => "max",
            var other => throw ExpressionTranslator.Untranslatable(p, $"PIVOT with {other}"),
        };
        var value = _x.Scalar(p.ValueColumns[0], c);
        var pivotRef = _x.Scalar(p.PivotColumn, c);
        var targets = others.Select(o => new PgTarget(new PgColumnRef(_x.TargetColumn(binding, o.Name)))).ToList();
        foreach (var inValue in p.InColumns)
        {
            PgExpr literal = TypeRules.IsNumeric(pivotType) ? new PgLiteral(inValue.Value) : PgLiteral.String(inValue.Value);
            targets.Add(new PgTarget(new PgFunctionCall(aggregate, [value]) { Filter = new PgBinary("=", pivotRef, literal) }, _names.Alias(inValue.Value)));
        }
        var query = new PgSelect
        {
            Targets = targets,
            From = [source],
            GroupBy = others.Select(o => (PgGroupItem)new PgGroupExpr(new PgColumnRef(_x.TargetColumn(binding, o.Name)))).ToList(),
        };
        return new PgSubqueryRef(query, _names.Alias(p.Alias?.Value ?? "pivoted"), []);
    }

    /// <summary>UNPIVOT (v FOR name IN (a, b)) as a lateral VALUES list of (name, value) pairs, without the NULL values (as UNPIVOT drops them).</summary>
    private PgFromItem Unpivot(UnpivotedTableReference u, ExpressionContext outer)
    {
        var inner = outer.Scope.Child();
        _model.BindTableReference(u.TableReference, inner, outer.Schema);
        var c = outer.With(inner);
        var source = FromItem(u.TableReference, c, outer);
        var binding = inner.Bindings.Single();
        var inNames = u.InColumns.Select(i => i.MultiPartIdentifier.Identifiers[^1].Value).ToList();
        var others = binding.Relation.Columns.Where(col => !inNames.Contains(col.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        string nameColumn = _names.Alias(u.PivotColumn.Value), valueColumn = _names.Alias(u.ValueColumn.Value);
        string qualifier = _x.AliasFor(binding);
        var rows = u.InColumns.Select((col, i) => (IReadOnlyList<PgExpr>)[PgLiteral.String(inNames[i]),
            new PgColumnRef(_x.TargetColumn(binding, inNames[i]), qualifier)]).ToList();
        var pairs = new PgSubqueryRef(new PgValues(rows), "unpivoted_pairs", [nameColumn, valueColumn], Lateral: true);
        var query = new PgSelect
        {
            Targets = [.. others.Select(o => new PgTarget(new PgColumnRef(_x.TargetColumn(binding, o.Name), qualifier))),
                new PgTarget(new PgColumnRef(nameColumn, "unpivoted_pairs")), new PgTarget(new PgColumnRef(valueColumn, "unpivoted_pairs"))],
            From = [new PgJoin("CROSS JOIN", source, pairs, null)],
            Where = new PgIsNull(new PgColumnRef(valueColumn, "unpivoted_pairs"), Negated: true),
        };
        return new PgSubqueryRef(query, _names.Alias(u.Alias?.Value ?? "unpivoted"), []);
    }

    // ---- subquery predicates ----

    private PgExpr? SubqueryPredicate(BooleanExpression e, ExpressionContext c) => e switch
    {
        ExistsPredicate ex => new PgExists(Query(ex.Subquery.QueryExpression, c)),
        InPredicate { Subquery: { } sq } inSub => new PgInSubquery(_x.Scalar(inSub.Expression, c), Query(sq.QueryExpression, c), inSub.NotDefined),
        SubqueryComparisonPredicate cmp when cmp.SubqueryComparisonPredicateType != SubqueryComparisonPredicateType.None =>
            new PgQuantified(_x.Scalar(cmp.Expression, c), ComparisonOperator(cmp.ComparisonType),
                cmp.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.All ? "ALL" : "ANY", Query(cmp.Subquery.QueryExpression, c)),
        BooleanComparisonExpression { SecondExpression: ScalarSubquery } => null,
        _ => null,
    };

    private static string ComparisonOperator(BooleanComparisonType t) => t switch
    {
        BooleanComparisonType.Equals => "=",
        BooleanComparisonType.GreaterThan => ">",
        BooleanComparisonType.LessThan => "<",
        BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => ">=",
        BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => "<=",
        _ => "<>",
    };

    /// <summary>Whether a query mentions a name in FROM (a CTE referring to itself is recursive).</summary>
    private static bool ReferencesName(QueryExpression q, string name)
    {
        var finder = new NameFinder(name);
        q.Accept(finder);
        return finder.Found;
    }

    private sealed class NameFinder(string name) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject.SchemaIdentifier is null && string.Equals(node.SchemaObject.BaseIdentifier.Value, name, StringComparison.OrdinalIgnoreCase))
                Found = true;
        }
    }
}
