using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>DML, result sets and SELECT assigning variables inside a body.</summary>
public sealed partial class ProceduralTranslator
{
    private IEnumerable<PgPlStatement> ResultSet(TSqlStatement s, BodyState st) => st.Signature.Kind switch
    {
        BodyKind.AnonymousBlock => [Untranslated(s, DiagnosticCodes.ResultSetInBlock,
            "This statement returns rows, and a DO block cannot: turn the batch into a function, or keep the rows in a temporary table and query it after the block.", st)],
        _ => [Untranslated(s, DiagnosticCodes.ProceduralStatement, "A result set returned by a routine is not translated yet.", st)],
    };

    private IEnumerable<PgPlStatement> Dml(TSqlStatement s, BodyState st)
    {
        var translated = _queries.Statement(s, st.C);
        if (translated is PgInsert { Returning.Count: > 0 } or PgUpdate { Returning.Count: > 0 } or PgDelete { Returning.Count: > 0 } or PgMerge { Returning.Count: > 0 })
            return ResultSet(s, st);

        var result = new List<PgPlStatement>();
        bool rowCountKnown = false;
        if (st.ReadsIdentity && s is InsertStatement insert && translated is PgInsert pgInsert && IdentityColumn(insert, st) is { } column)
        {
            var identity = Helper(IdentityVariable, "numeric(38,0)", st);
            var returning = pgInsert with { Returning = [new PgTarget(new PgColumnRef(column))] };
            if (insert.InsertSpecification.InsertSource is ValuesInsertSource { IsDefaultValues: true } or ValuesInsertSource { RowValues.Count: 1 })
            {
                result.Add(new PgSqlStatement(returning) { Into = [identity] });
            }
            else
            {
                // Several rows: the last identity is the largest one inserted. The count comes from the
                // same query, since the statement's own row count is the SELECT's.
                var targets = new List<PgTarget> { new(new PgFunctionCall("max", [new PgColumnRef(column)])) };
                var into = new List<PgExpr> { identity };
                if (st.ReadsRowCount)
                {
                    targets.Add(new PgTarget(new PgCast(new PgFunctionCall("count", []) { Star = true }, "integer")));
                    into.Add(Helper(RowCountVariable, "integer", st));
                    rowCountKnown = true;
                }
                var select = new PgSelect
                {
                    With = [new PgCte("sql2pg_inserted", [], new PgModifyingQuery(returning))],
                    Targets = targets,
                    From = [new PgTableRef(new PgName("", "sql2pg_inserted"))],
                };
                result.Add(new PgSqlStatement(new PgQueryStatement(select)) { Into = into });
            }
        }
        else
        {
            result.Add(new PgSqlStatement(translated));
        }
        if (st.ReadsRowCount && !rowCountKnown)
            result.Add(RowCount(st));
        return result;
    }

    private PgGetDiagnostics RowCount(BodyState st) => new([new(Helper(RowCountVariable, "integer", st).Name, "ROW_COUNT")]);

    /// <summary>The PostgreSQL name of the identity column of an INSERT's target table, if it has one.</summary>
    private string? IdentityColumn(InsertStatement insert, BodyState st)
    {
        if (insert.InsertSpecification.Target is not NamedTableReference target || target.SchemaObject.BaseIdentifier.Value.StartsWith('#'))
            return null;
        var name = _model.ResolveRelation(target.SchemaObject, st.C.Schema);
        var table = name is null ? null : _model.Table(name);
        var identity = table?.Columns.FirstOrDefault(col => col.Identity is not null);
        return identity is null ? null : _names.Column(table!.Name, identity.Name);
    }

    // ---- SELECT @v = … ----

    /// <summary>
    /// <c>SELECT @a = x, @b = y FROM …</c>. SQL Server assigns from each row in turn, so the last
    /// row's values remain, and a query returning no rows leaves the variables as they were. That
    /// is a FOR loop over the rows; a plain <c>SELECT … INTO</c> is used where it gives the same
    /// answer (exactly one row, or at most one into variables not yet assigned).
    /// </summary>
    private IEnumerable<PgPlStatement> SelectAssign(SelectStatement select, QuerySpecification spec, BodyState st)
    {
        var c = st.C;
        if (select.Into is not null || spec.SelectElements.Any(e => e is not SelectSetVariable))
            throw ExpressionTranslator.Untranslatable(select, "SELECT mixing variable assignments with other columns");
        var sets = spec.SelectElements.Cast<SelectSetVariable>().ToList();
        var targets = sets.Select(set =>
        {
            var (target, type, dataType) = Target(set.Variable, st);
            return (Target: target, Value: Converted(Compound(set.Variable, set.AssignmentKind, set.Expression), type, dataType, c));
        }).ToList();
        var names = sets.Select(set => set.Variable.Name.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<PgPlStatement>();

        if (spec.FromClause is null)
        {
            // No rows to read: the assignments in turn (WHERE without FROM decides whether they happen).
            var assignments = targets.Select(t => (PgPlStatement)new PgAssign(t.Target, _x.Scalar(t.Value, c))).ToList();
            if (spec.WhereClause is null)
            {
                result.AddRange(assignments);
                if (st.ReadsRowCount)
                    result.Add(new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("1")));
            }
            else
            {
                if (st.ReadsRowCount)
                    assignments.Add(new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("1")));
                result.Add(new PgIf([new PgIfBranch(_x.Boolean(spec.WhereClause.SearchCondition, c), assignments)],
                    st.ReadsRowCount ? [new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("0"))] : null));
            }
            st.Assigned.UnionWith(names);
            return result;
        }

        var values = targets.Select(t => t.Value).ToList();
        bool accumulates = values.Any(v => References(v, names));
        bool exactlyOne = spec.GroupByClause is null && spec.HavingClause is null && values.Any(HasAggregate);
        bool atMostOne = AtMostOneRow(spec, c);

        if (exactlyOne || !accumulates && atMostOne && st.LoopDepth == 0 && !names.Any(st.Assigned.Contains))
        {
            var query = _queries.Query(Clone(spec, values, alias: false), c, select.WithCtesAndXmlNamespaces);
            result.Add(new PgSqlStatement(new PgQueryStatement(query)) { Into = targets.Select(t => t.Target).ToList() });
            if (st.ReadsRowCount)
                result.Add(RowCount(st));
            st.Assigned.UnionWith(names);
            return result;
        }

        if (!atMostOne && spec.OrderByClause is null)
            c.Report(DiagnosticCodes.SelectAssignmentOrder, Severity.Warning,
                "SELECT assigns variables from a query that may return several rows and has no ORDER BY: the last row's values remain, and which row is last is arbitrary.");
        var row = Helper(RowVariable, "record", st);
        PgQuery rows;
        List<PgPlStatement> body;
        if (!accumulates)
        {
            rows = _queries.Query(Clone(spec, values, alias: true), c, select.WithCtesAndXmlNamespaces);
            body = targets.Select((t, k) => (PgPlStatement)new PgAssign(t.Target, new PgColumnRef(Field(k), row.Name))).ToList();
        }
        else
        {
            (rows, body) = Accumulation(select, spec, targets, st);
        }

        if (st.ReadsRowCount)
        {
            var count = Helper(RowCountVariable, "integer", st);
            result.Add(new PgAssign(count, new PgLiteral("0")));
            body.Add(new PgAssign(count, new PgBinary("+", count, new PgLiteral("1"))));
        }
        result.Add(new PgForQuery(row.Name, rows, body));
        st.Assigned.UnionWith(names);
        return result;
    }

    private static string Field(int k) => $"sql2pg_c{k + 1}";

    /// <summary>
    /// <c>SELECT @s = @s + name FROM t</c>: each row's value depends on the previous row's, so the
    /// loop reads the columns the expressions use and computes the assignments itself, in order.
    /// </summary>
    private (PgQuery Rows, List<PgPlStatement> Body) Accumulation(SelectStatement select, QuerySpecification spec,
        List<(PgExpr Target, ScalarExpression Value)> targets, BodyState st)
    {
        var c = st.C;
        if (spec.GroupByClause is not null || spec.HavingClause is not null || spec.UniqueRowFilter == UniqueRowFilter.Distinct
            || select.WithCtesAndXmlNamespaces is { CommonTableExpressions.Count: > 0 } || targets.Any(t => HasAggregate(t.Value)))
            throw ExpressionTranslator.Untranslatable(select, "SELECT accumulating into a variable over a grouped, distinct or CTE query",
                DiagnosticCodes.SelectAssignmentAccumulates);

        var scope = _model.QueryScope(spec, c.Scope, c.Schema);
        var inQuery = c.With(scope);
        var expressions = targets.Select(t => _x.Scalar(t.Value, inQuery)).ToList();
        if (expressions.Any(e => PgExprRewriter.Any(e, x => x is PgSubquery or PgExists or PgInSubquery or PgQuantified or PgFunctionCall { Over: not null })))
            throw ExpressionTranslator.Untranslatable(select, "SELECT accumulating into a variable with a subquery or window function",
                DiagnosticCodes.SelectAssignmentAccumulates);

        var fields = new Dictionary<PgColumnRef, string>();
        PgExpr? ToField(PgExpr x)
        {
            if (x is not PgColumnRef column || st.VariableRefs.Contains(x))
                return null;
            if (!fields.TryGetValue(column, out var field))
                fields[column] = field = Field(fields.Count);
            return new PgColumnRef(field, RowVariable);
        }
        var body = targets.Select((t, k) => (PgPlStatement)new PgAssign(t.Target, PgExprRewriter.Rewrite(expressions[k], ToField))).ToList();

        var one = new IntegerLiteral { Value = "1" };
        var rows = (PgSelect)_queries.Query(Clone(spec, [one], alias: false), c);
        rows = rows with { Targets = fields.Select(f => new PgTarget(f.Key, f.Value)).ToList() };
        return (rows, body);
    }

    /// <summary>The query with its assignments replaced by plain columns (named <c>sql2pg_cN</c> when <paramref name="alias"/>).</summary>
    private static QuerySpecification Clone(QuerySpecification spec, IReadOnlyList<ScalarExpression> values, bool alias)
    {
        var clone = new QuerySpecification
        {
            UniqueRowFilter = spec.UniqueRowFilter,
            TopRowFilter = spec.TopRowFilter,
            FromClause = spec.FromClause,
            WhereClause = spec.WhereClause,
            GroupByClause = spec.GroupByClause,
            HavingClause = spec.HavingClause,
            OrderByClause = spec.OrderByClause,
            OffsetClause = spec.OffsetClause,
            WindowClause = spec.WindowClause,
        };
        for (int k = 0; k < values.Count; k++)
            clone.SelectElements.Add(new SelectScalarExpression
            {
                Expression = values[k],
                ColumnName = alias ? new IdentifierOrValueExpression { Identifier = new Identifier { Value = Field(k) } } : null,
            });
        return clone;
    }

    /// <summary>
    /// Whether the query cannot return more than one row: TOP (1), or a single table whose unique
    /// key's columns are all compared for equality with values that do not depend on the table.
    /// </summary>
    private bool AtMostOneRow(QuerySpecification spec, ExpressionContext c)
    {
        if (spec.TopRowFilter is { Percent: false } top && Unwrap(top.Expression) is IntegerLiteral { Value: "1" })
            return true;
        if (spec.GroupByClause is not null || spec.FromClause?.TableReferences is not [NamedTableReference table] || spec.WhereClause is null)
            return false;
        var name = _model.ResolveRelation(table.SchemaObject, c.Schema);
        var model = name is null ? null : _model.Table(name);
        if (model is null)
            return false;
        string alias = table.Alias?.Value ?? table.SchemaObject.BaseIdentifier.Value;
        var equated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectEqualities(spec.WhereClause.SearchCondition, alias, equated);
        var keys = new List<IEnumerable<string>>();
        if (model.PrimaryKey is { } pk)
            keys.Add(pk.Columns.Select(k => k.Name));
        keys.AddRange(model.Uniques.Select(u => u.Columns.Select(k => k.Name)));
        keys.AddRange(model.Indexes.Where(i => i.IsUnique && i.FilterExpression is null).Select(i => i.Keys.Select(k => k.Name)));
        return keys.Any(key => key.All(equated.Contains));
    }

    private static ScalarExpression Unwrap(ScalarExpression e) => e is ParenthesisExpression p ? Unwrap(p.Expression) : e;

    private static void CollectEqualities(BooleanExpression e, string alias, HashSet<string> columns)
    {
        switch (e)
        {
            case BooleanParenthesisExpression p:
                CollectEqualities(p.Expression, alias, columns);
                break;
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                CollectEqualities(and.FirstExpression, alias, columns);
                CollectEqualities(and.SecondExpression, alias, columns);
                break;
            case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } eq:
                if (ColumnOf(eq.FirstExpression, alias) is { } left && !HasColumns(eq.SecondExpression))
                    columns.Add(left);
                if (ColumnOf(eq.SecondExpression, alias) is { } right && !HasColumns(eq.FirstExpression))
                    columns.Add(right);
                break;
        }
    }

    private static string? ColumnOf(ScalarExpression e, string alias) =>
        Unwrap(e) is ColumnReferenceExpression { ColumnType: ColumnType.Regular, MultiPartIdentifier.Identifiers: var parts }
        && (parts.Count == 1 || string.Equals(parts[^2].Value, alias, StringComparison.OrdinalIgnoreCase))
            ? parts[^1].Value
            : null;

    private static bool HasColumns(TSqlFragment e)
    {
        var finder = new Finder(f => f is ColumnReferenceExpression);
        e.Accept(finder);
        return finder.Found;
    }

    private static bool References(TSqlFragment e, HashSet<string> variables)
    {
        var finder = new Finder(f => f is VariableReference v && variables.Contains(v.Name.TrimStart('@')));
        e.Accept(finder);
        return finder.Found;
    }

    /// <summary>An aggregate call outside any subquery (a window aggregate does not count).</summary>
    private static bool HasAggregate(TSqlFragment e)
    {
        var finder = new Finder(f => f is FunctionCall { OverClause: null } call
            && ExpressionTranslator.IsAggregate(call.FunctionName.Value.ToUpperInvariant()), skipSubqueries: true);
        e.Accept(finder);
        return finder.Found;
    }

    private sealed class Finder(Func<TSqlFragment, bool> test, bool skipSubqueries = false) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(TSqlFragment node) => Found |= test(node);

        public override void ExplicitVisit(ScalarSubquery node)
        {
            if (!skipSubqueries)
                base.ExplicitVisit(node);
        }
    }
}
