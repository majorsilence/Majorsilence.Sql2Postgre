using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// DML: INSERT, UPDATE (and UPDATE … FROM with joins), DELETE (and DELETE … FROM), MERGE and
/// SELECT … INTO. OUTPUT becomes RETURNING, with <c>inserted</c>/<c>deleted</c> as PostgreSQL
/// 18's <c>new</c>/<c>old</c>. Values written to a column are converted to its type as SQL Server
/// converts them (0/1 to boolean for a bit column, strings to numbers and dates).
/// </summary>
public sealed partial class QueryTranslator
{
    /// <summary>A SELECT, INSERT, UPDATE, DELETE or MERGE statement.</summary>
    public PgStatement Statement(TSqlStatement statement, ExpressionContext c)
    {
        if (statement is StatementWithCtesAndXmlNamespaces { OptimizerHints.Count: > 0 })
            c.Report(DiagnosticCodes.HintDropped, Severity.Info, "OPTION (…) query hints are dropped.");
        return statement switch
        {
            SelectStatement { Into: { } into } s => SelectInto(s, into, c),
            SelectStatement s => new PgQueryStatement(Query(s.QueryExpression, c, s.WithCtesAndXmlNamespaces)),
            InsertStatement i => Insert(i, c),
            UpdateStatement u => Update(u, c),
            DeleteStatement d => Delete(d, c),
            MergeStatement m => Merge(m, c),
            TruncateTableStatement t => Truncate(t, c),
            _ => throw ExpressionTranslator.Untranslatable(statement, "this statement"),
        };
    }

    private PgStatement SelectInto(SelectStatement s, SchemaObjectName into, ExpressionContext c)
    {
        var query = Query(s.QueryExpression, c, s.WithCtesAndXmlNamespaces);
        string written = into.BaseIdentifier.Value;
        GlobalTemp(into, written);
        if (written.StartsWith('#'))
        {
            // Later statements of the batch can use the temp table: the scope remembers its columns.
            c.Scope.DefineTable(written, new RelationInfo(null, ObjectKind.Table,
                _model.OutputColumns(s.QueryExpression, c.Scope, c.Schema)));
            return new PgCreateTableAs(new PgName("", TempTable(written)), query, Temporary: true);
        }
        var name = new ObjectName(into.SchemaIdentifier?.Value ?? c.Schema ?? SemanticModel.DefaultSchema, written);
        return new PgCreateTableAs(_names.Relation(name), query, Temporary: false);
    }

    // ---- targets ----

    /// <summary>The table a DML statement writes, its columns, and the binding its scope knows it by.</summary>
    private sealed record DmlTarget(PgName Name, ObjectName? Source, RelationInfo Relation, string? Alias);

    private DmlTarget TargetOf(TableReference target, ExpressionContext c, string? alias = null)
    {
        if (target is VariableTableReference v && TableVariable?.Invoke(v.Variable.Name) is { } table)
            return new DmlTarget(new PgName("", table), null, c.Scope.Table(v.Variable.Name) ?? new RelationInfo(null, ObjectKind.Table, []),
                alias ?? v.Alias?.Value);
        if (target is not NamedTableReference n)
            throw ExpressionTranslator.Untranslatable(target, "a DML target that is not a table");
        string written = n.SchemaObject.BaseIdentifier.Value;
        GlobalTemp(n, written);
        if (written.StartsWith('#'))
            return new DmlTarget(new PgName("", TempTable(written)), null, c.Scope.Table(written) ?? new RelationInfo(null, ObjectKind.Table, []), alias ?? n.Alias?.Value);
        if (_model.IsExternal(n.SchemaObject))
            throw ExpressionTranslator.Untranslatable(n, "a table in another database or on a linked server", DiagnosticCodes.ExternalReference);
        var name = _model.ResolveRelation(n.SchemaObject, c.Schema)!;
        var relation = _model.Relation(name) ?? new RelationInfo(name, ObjectKind.Table, []);
        return new DmlTarget(_names.Relation(name), name, relation, alias ?? n.Alias?.Value);
    }

    private string Column(DmlTarget target, string column) =>
        target.Source is { } table ? _names.Column(table, column) : _names.Alias(column);

    /// <summary>
    /// A value written to a column, converted as SQL Server would convert it to the column's type:
    /// 0/1 to false/true for a bit; a string expression to the column's number, date or uuid type.
    /// </summary>
    private PgExpr Assigned(ScalarExpression value, SqlServerType? column, ExpressionContext c)
    {
        if (value is DefaultLiteral)
            return new PgKeyword("DEFAULT");
        var translated = _x.Scalar(value, c);
        if (column?.Name == "bit")
            return ExpressionTranslator.AsBoolean(translated);
        var valueType = _x.TypeOf(value, c);
        if (column is not null && value is not (StringLiteral or NullLiteral) && TypeRules.IsString(valueType)
            && (TypeRules.IsNumeric(column) || TypeRules.IsDateTime(column) || column.Name == "uniqueidentifier"))
            return new PgCast(translated, _x.Types.Map(column).PgType);
        if (column is not null && valueType?.Name == "bit" && TypeRules.IsNumeric(column))
            return new PgCast(translated, "integer");
        return translated;
    }

    /// <summary>An INSERT without a column list fills every column but identity, computed and rowversion columns, as SQL Server does.</summary>
    private IReadOnlyList<string> InsertableColumns(DmlTarget target) =>
        target.Source is { } name && _model.Table(name) is { } table
            ? table.Columns.Where(col => col.Identity is null && col.Computed is null && col.Type.Name is not ("timestamp" or "rowversion")).Select(col => col.Name).ToList()
            : target.Relation.Columns.Select(col => col.Name).ToList();

    // ---- OUTPUT ----

    private enum Dml { Insert, Update, Delete, Merge }

    /// <summary>The alias of the target's old row, joined to an UPDATE whose OUTPUT reads deleted before PostgreSQL 18.</summary>
    private const string OldRowAlias = "sql2pg_old";

    /// <summary>The old row as one composite column of <see cref="OldRowAlias"/>, so its column names hide no others.</summary>
    private const string OldRowColumn = "sql2pg_row";

    private static PgExpr OldRow => new PgColumnRef(OldRowColumn, OldRowAlias);

    /// <summary>
    /// The RETURNING list of OUTPUT, or of OUTPUT … INTO (whose rows <see cref="OutputInto"/> then
    /// writes). <paramref name="qualifier"/> is the name the target goes by in the statement.
    /// From PostgreSQL 18, <c>inserted</c> and <c>deleted</c> are RETURNING's <c>new</c> and
    /// <c>old</c>. Before 18, RETURNING sees the target row only: the new row of INSERT and
    /// UPDATE, the removed row of DELETE. So an UPDATE's <c>deleted</c> reads a self-joined copy
    /// of the old row (<paramref name="oldRow"/> is then set, and <see cref="JoinOldRow"/> adds
    /// it); MERGE has RETURNING from 17, where the target's columns are NULL for the rows the
    /// pseudo-table does not see.
    /// </summary>
    private IReadOnlyList<PgTarget> Returning(OutputClause? output, OutputIntoClause? into, DmlTarget target, ExpressionContext c, Scope? scope,
        Dml kind, string? qualifier, out bool oldRow, bool mergeUpdates = false)
    {
        oldRow = false;
        if (into is not null && output is not null)
            throw ExpressionTranslator.Untranslatable(into, "OUTPUT … INTO together with a plain OUTPUT", DiagnosticCodes.OutputInto);
        var columns = into?.SelectColumns ?? output?.SelectColumns;
        if (columns is null)
            return [];
        int version = _x.Options.TargetVersion;
        if (kind == Dml.Merge && version < 17)
            throw ExpressionTranslator.Untranslatable((TSqlFragment?)into ?? output!,
                $"MERGE … OUTPUT on PostgreSQL {version} (MERGE has RETURNING from PostgreSQL 17)", DiagnosticCodes.OutputBefore18);
        bool usesOld = false;

        PgExpr Column(string pseudo, string column)
        {
            if (version >= 18)
                return new PgColumnRef(column, pseudo == "inserted" ? "new" : "old");
            switch (kind)
            {
                case Dml.Update when pseudo == "deleted":
                    usesOld = true;
                    return new PgTemplate("({0})." + NameMapper.Quote(column), [OldRow]);
                case Dml.Merge when pseudo == "deleted" && mergeUpdates:
                    throw new UntranslatableException(DiagnosticCodes.OutputBefore18,
                        $"OUTPUT deleted.{column} in a MERGE that updates cannot be translated for PostgreSQL {version}: its RETURNING sees only the new row of an update (old is PostgreSQL 18's).");
                case Dml.Merge:
                    // SQL Server's inserted is NULL for a deleted row, and deleted for an inserted one.
                    return new PgCase(null,
                        [new PgWhen(new PgBinary("=", new PgFunctionCall("merge_action", []), PgLiteral.String(pseudo == "inserted" ? "DELETE" : "INSERT")), PgLiteral.Null)],
                        new PgColumnRef(column, qualifier));
                default:
                    return new PgColumnRef(column, qualifier);
            }
        }

        // inserted.* / deleted.*: one star, except in MERGE before 18, where each column is wrapped.
        IReadOnlyList<PgTarget> Star(string pseudo)
        {
            if (version >= 18)
                return [PgTarget.All(pseudo == "deleted" ? "old" : "new")];
            if (kind == Dml.Update && pseudo == "deleted")
            {
                usesOld = true;
                return [new PgTarget(new PgTemplate("({0}).*", [OldRow]))];
            }
            if (kind != Dml.Merge)
                return [PgTarget.All(qualifier ?? target.Name.Name)];
            if (target.Relation.Columns.Count == 0)
                throw new UntranslatableException(DiagnosticCodes.OutputBefore18,
                    $"OUTPUT {pseudo}.* in a MERGE into a table whose columns are not known cannot be translated for PostgreSQL {version}.");
            var binding = new Binding(pseudo, target.Relation);
            return target.Relation.Columns.Select(col =>
            {
                string mapped = _x.TargetColumn(binding, col.Name);
                return new PgTarget(Column(pseudo, mapped), mapped);
            }).ToList();
        }

        var pseudoScope = (scope ?? c.Scope).Child();
        pseudoScope.Bind(new Binding("inserted", target.Relation));
        pseudoScope.Bind(new Binding("deleted", target.Relation));
        var rc = c.WithReturning(pseudoScope, Column);
        var targets = columns.SelectMany(e => e switch
        {
            SelectScalarExpression { Expression: ColumnReferenceExpression { ColumnType: ColumnType.PseudoColumnAction } } s =>
                [new PgTarget(new PgFunctionCall("merge_action", []), s.ColumnName is null ? null : _names.Alias(s.ColumnName.Value))],
            SelectScalarExpression s => [ReturningTarget(s, rc)],
            SelectStarExpression { Qualifier.Identifiers: [var q] } => Star(q.Value.ToLowerInvariant()),
            _ => throw ExpressionTranslator.Untranslatable(e, "this OUTPUT item"),
        }).ToList();
        oldRow = usesOld;
        return targets;
    }

    /// <summary>An OUTPUT expression; a column wrapped for MERGE keeps the column's name, as SQL Server's does.</summary>
    private PgTarget ReturningTarget(SelectScalarExpression s, ExpressionContext rc)
    {
        var value = _x.Scalar(s.Expression, rc);
        string? alias = s.ColumnName is not null ? _names.Alias(s.ColumnName.Value)
            : value is PgCase && s.Expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., var last] } ? _names.Alias(last.Value)
            : null;
        return new PgTarget(value, alias);
    }

    /// <summary>
    /// Before PostgreSQL 18: an UPDATE whose OUTPUT reads deleted joins the target to a copy of
    /// itself, by primary key (or by ctid when the table has none), so RETURNING can read the row
    /// as it was before the SET: <c>FROM (SELECT sql2pg_old AS sql2pg_row, sql2pg_old.key AS
    /// sql2pg_k1 FROM t AS sql2pg_old) AS sql2pg_old WHERE sql2pg_old.sql2pg_k1 = t.key</c>. The
    /// copy shows only those names, so the statement's unqualified columns stay unambiguous.
    /// </summary>
    private PgUpdate JoinOldRow(PgUpdate update, DmlTarget target, string qualifier, ExpressionContext c)
    {
        int version = _x.Options.TargetVersion;
        if (update.Where is PgKeyword { Sql: var w } && w.StartsWith("CURRENT OF", StringComparison.Ordinal))
            throw new UntranslatableException(DiagnosticCodes.OutputBefore18,
                $"OUTPUT deleted in UPDATE … WHERE CURRENT OF cannot be translated for PostgreSQL {version} (it reads the old row through a join).");
        IReadOnlyList<string>? key = target.Source is { } source && _model.Table(source)?.PrimaryKey is { Columns.Count: > 0 } pk
            ? pk.Columns.Select(col => Column(target, col.Name)).ToList()
            : null;
        var keys = key ?? ["ctid"];
        var names = keys.Select((_, i) => $"sql2pg_k{i + 1}").ToList();
        PgExpr? on = null;
        for (int i = 0; i < keys.Count; i++)
        {
            PgExpr eq = new PgBinary("=", new PgColumnRef(names[i], OldRowAlias), new PgColumnRef(keys[i], qualifier));
            on = on is null ? eq : new PgBinary("AND", on, eq);
        }
        var copy = new PgSelect
        {
            Targets = [new PgTarget(new PgColumnRef(OldRowAlias), OldRowColumn), .. keys.Select((k, i) => new PgTarget(new PgColumnRef(k, OldRowAlias), names[i]))],
            From = [new PgTableRef(target.Name, OldRowAlias)],
        };
        if (key is null)
            c.Report(DiagnosticCodes.OutputBefore18, Severity.Warning,
                $"OUTPUT deleted in this UPDATE reads the old row through a self-join on ctid (PostgreSQL {version} has no old in RETURNING, and {target.Name.Name} has no primary key): a row that another transaction updates while this one waits for it is skipped. Add a primary key, or target PostgreSQL 18.");
        else
            c.Report(DiagnosticCodes.OutputBefore18, Severity.Info,
                $"OUTPUT deleted in this UPDATE reads the old row through a self-join on the primary key (PostgreSQL {version} has no old in RETURNING).");
        return update with
        {
            From = [.. update.From, new PgSubqueryRef(copy, OldRowAlias, [])],
            Where = update.Where is null ? on : new PgBinary("AND", on!, update.Where),
        };
    }

    /// <summary>
    /// OUTPUT … INTO t: the changed rows written to t by the same statement, through a
    /// data-modifying CTE — <c>WITH sql2pg_output (c1, …) AS (dml RETURNING …) INSERT INTO t (…)
    /// SELECT c1, … FROM sql2pg_output</c>. The statement's own CTEs move up beside it.
    /// </summary>
    private PgStatement OutputInto(PgStatement dml, OutputIntoClause? into, ExpressionContext c)
    {
        if (into is null)
            return dml;
        var table = TargetOf(into.IntoTable, c);
        var (inner, ctes, returning) = dml switch
        {
            PgInsert i => ((PgStatement)(i with { With = [] }), i.With, i.Returning),
            PgUpdate u => (u with { With = [] }, u.With, u.Returning),
            PgDelete d => (d with { With = [] }, d.With, d.Returning),
            PgMerge m => (m with { With = [] }, m.With, m.Returning),
            _ => throw ExpressionTranslator.Untranslatable(into, "OUTPUT … INTO here", DiagnosticCodes.OutputInto),
        };
        var columns = into.IntoTableColumns.Select(col => Column(table, col.MultiPartIdentifier.Identifiers[^1].Value)).ToList();
        bool star = returning.Any(t => t.Star);
        List<string> names = star ? [] : Enumerable.Range(1, returning.Count).Select(k => $"c{k}").ToList();
        var rows = new PgSelect
        {
            Targets = star ? [PgTarget.All()] : names.Select(n => new PgTarget(new PgColumnRef(n))).ToList(),
            From = [new PgTableRef(new PgName("", "sql2pg_output"))],
        };
        return new PgInsert(table.Name, columns, rows) { With = [.. ctes, new PgCte("sql2pg_output", names, new PgModifyingQuery(inner))] };
    }

    /// <summary>
    /// The PostgreSQL cursor variable for a T-SQL cursor, or null when it is not one the procedural
    /// translator declared (set by it).
    /// </summary>
    public Func<CursorId, string?>? CursorVariable { get; set; }

    /// <summary>WHERE CURRENT OF cursor: the row the cursor is on.</summary>
    private PgExpr CurrentOfCursor(WhereClause where, CursorId cursor, PgExpr? joins)
    {
        if (CursorVariable?.Invoke(cursor) is not { } variable)
            throw ExpressionTranslator.Untranslatable(where, "WHERE CURRENT OF a cursor not declared in this code", DiagnosticCodes.ProceduralGlobal);
        if (joins is not null)
            throw ExpressionTranslator.Untranslatable(where, "WHERE CURRENT OF with joined tables", DiagnosticCodes.ProceduralStatement);
        return new PgKeyword("CURRENT OF " + NameMapper.Quote(variable));
    }

    // ---- TRUNCATE ----

    /// <summary>TRUNCATE TABLE also restarts the table's identity, as SQL Server's does.</summary>
    private PgStatement Truncate(TruncateTableStatement t, ExpressionContext c)
    {
        if (t.PartitionRanges.Count > 0)
            throw ExpressionTranslator.Untranslatable(t, "TRUNCATE TABLE … WITH (PARTITIONS …)");
        var target = TargetOf(new NamedTableReference { SchemaObject = t.TableName }, c);
        return new PgTruncate(target.Name, RestartIdentity: true);
    }

    /// <summary>A global temporary table (<c>##t</c>) is shared by every session; PostgreSQL has none.</summary>
    internal static void GlobalTemp(TSqlFragment fragment, string written)
    {
        if (written.StartsWith("##", StringComparison.Ordinal))
            throw ExpressionTranslator.Untranslatable(fragment,
                $"The global temporary table {written}, which every session shares (PostgreSQL's temporary tables belong to one session; use a real table),",
                DiagnosticCodes.GlobalTempTable);
    }

    // ---- INSERT ----

    private PgStatement Insert(InsertStatement statement, ExpressionContext c)
    {
        var spec = statement.InsertSpecification;
        if (spec.TopRowFilter is not null)
            throw ExpressionTranslator.Untranslatable(spec.TopRowFilter, "INSERT TOP (n)", DiagnosticCodes.DmlTop);
        var target = TargetOf(spec.Target, c);
        var columns = spec.Columns.Count > 0
            ? spec.Columns.Select(col => col.MultiPartIdentifier.Identifiers[^1].Value).ToList()
            : InsertableColumns(target);
        var types = columns.Select(col => target.Relation.Column(col)?.Type).ToList();

        PgQuery? source = spec.InsertSource switch
        {
            ValuesInsertSource { IsDefaultValues: true } => null,
            ValuesInsertSource v => new PgValues(v.RowValues.Select(row => (IReadOnlyList<PgExpr>)row.ColumnValues
                .Select((value, i) => Assigned(value, i < types.Count ? types[i] : null, c)).ToList()).ToList()),
            SelectInsertSource s => InsertSelect(s.Select, types, c),
            ExecuteInsertSource e => ExecuteSource?.Invoke(e, c)
                                     ?? throw ExpressionTranslator.Untranslatable(e, "INSERT … EXEC", DiagnosticCodes.InsertExec),
            var other => throw ExpressionTranslator.Untranslatable(other, "this INSERT source"),
        };
        return OutputInto(new PgInsert(target.Name, source is null ? [] : columns.Select(col => Column(target, col)).ToList(), source)
        {
            With = Ctes(statement.WithCtesAndXmlNamespaces, c, out _),
            Returning = Returning(spec.OutputClause, spec.OutputIntoClause, target, c, null, Dml.Insert, target.Name.Name, out _),
        }, spec.OutputIntoClause, c);
    }

    /// <summary>INSERT … SELECT: the selected values converted to the target columns' types.</summary>
    private PgQuery InsertSelect(QueryExpression select, IReadOnlyList<SqlServerType?> types, ExpressionContext c)
    {
        var query = Query(select, c);
        if (select is not QuerySpecification spec || query is not PgSelect s || s.Targets.Count != types.Count)
            return query;
        var scope = _model.QueryScope(spec, c.Scope, c.Schema);
        var targets = s.Targets.Select((t, i) => spec.SelectElements[i] is SelectScalarExpression e
            ? t with { Expression = Assigned(e.Expression, types[i], c.With(scope)) }
            : t).ToList();
        return s with { Targets = targets };
    }

    private IReadOnlyList<PgCte> Ctes(WithCtesAndXmlNamespaces? with, ExpressionContext c, out Scope scope)
    {
        scope = c.Scope;
        if (with is not { CommonTableExpressions.Count: > 0 })
            return [];
        scope = c.Scope.Child();
        _model.DefineCtes(with, scope, c.Schema);
        var cc = c.With(scope);
        return with.CommonTableExpressions.Select(cte => new PgCte(_names.Alias(cte.ExpressionName.Value),
            cte.Columns.Select(col => _names.Alias(col.Value)).ToList(), Query(cte.QueryExpression, cc))).ToList();
    }

    // ---- UPDATE and DELETE ----

    /// <summary>
    /// The FROM clause of UPDATE … FROM / DELETE … FROM rearranged as PostgreSQL wants it: the
    /// target is named once, in the statement, and must not appear in FROM; the conditions that
    /// joined it to the rest move into WHERE. Only inner (and cross) joins can be rearranged so.
    /// </summary>
    private (string? Alias, IReadOnlyList<PgFromItem> From, PgExpr? JoinConditions, Scope Scope) TargetAndFrom(
        DmlTarget target, FromClause? from, ExpressionContext c)
    {
        var scope = c.Scope.Child();
        if (from is null)
        {
            scope.Bind(new Binding(target.Alias ?? target.Source?.Name ?? target.Name.Name, target.Relation));
            return (target.Alias is null ? null : _names.Alias(target.Alias), [], null, scope);
        }
        foreach (var r in from.TableReferences)
            _model.BindTableReference(r, scope, c.Schema);
        var fc = c.With(scope);

        // The binding the target names: its alias, or the table itself.
        string targetWritten = target.Alias ?? target.Source?.Name ?? target.Name.Name;
        var leaves = new List<TableReference>();
        var conditions = new List<BooleanExpression>();
        foreach (var r in from.TableReferences)
            Flatten(r, leaves, conditions);
        var targetLeaf = leaves.FirstOrDefault(l => l is NamedTableReference n
            && (string.Equals(n.Alias?.Value, targetWritten, StringComparison.OrdinalIgnoreCase)
                || (n.Alias is null && string.Equals(n.SchemaObject.BaseIdentifier.Value, targetWritten, StringComparison.OrdinalIgnoreCase))));
        if (targetLeaf is null)
        {
            if (ContainsTarget(from, targetWritten))
                throw ExpressionTranslator.Untranslatable(from, "UPDATE/DELETE whose target is inside an outer join", DiagnosticCodes.UpdateFromOuterJoin);
            // The target is not in FROM at all: FROM is just more tables (rare, but valid T-SQL).
            scope.Bind(new Binding(targetWritten, target.Relation));
            return (target.Alias is null ? null : _names.Alias(target.Alias), leaves.Select(l => FromItem(l, fc, c)).ToList(),
                And(conditions.Select(cond => _x.Boolean(cond, fc))), scope);
        }
        var alias = ((NamedTableReference)targetLeaf).Alias?.Value;
        var others = leaves.Where(l => !ReferenceEquals(l, targetLeaf)).Select(l => FromItem(l, fc, c)).ToList();
        return (alias is null ? null : _names.Alias(alias), others, And(conditions.Select(cond => _x.Boolean(cond, fc))), scope);
    }

    /// <summary>Inner and cross joins split into their tables and ON conditions; anything else stays one leaf.</summary>
    private static void Flatten(TableReference r, List<TableReference> leaves, List<BooleanExpression> conditions)
    {
        switch (r)
        {
            case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.Inner } j:
                Flatten(j.FirstTableReference, leaves, conditions);
                Flatten(j.SecondTableReference, leaves, conditions);
                conditions.Add(j.SearchCondition);
                break;
            case UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossJoin } j:
                Flatten(j.FirstTableReference, leaves, conditions);
                Flatten(j.SecondTableReference, leaves, conditions);
                break;
            case JoinParenthesisTableReference p:
                Flatten(p.Join, leaves, conditions);
                break;
            default:
                leaves.Add(r);
                break;
        }
    }

    private static bool ContainsTarget(TSqlFragment from, string name)
    {
        var aliasFinder = new AliasFinder(name);
        from.Accept(aliasFinder);
        return aliasFinder.Found;
    }

    /// <summary>A table under this alias, or unaliased under this name (<c>Item</c>, <c>dbo.Item</c>).</summary>
    private sealed class AliasFinder(string alias) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(NamedTableReference node)
        {
            if (string.Equals(node.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase)
                || node.Alias is null && string.Equals(node.SchemaObject.BaseIdentifier.Value, alias, StringComparison.OrdinalIgnoreCase))
                Found = true;
        }
    }

    private static PgExpr? And(IEnumerable<PgExpr?> parts)
    {
        PgExpr? result = null;
        foreach (var p in parts.Where(p => p is not null))
            result = result is null ? p : new PgBinary("AND", result, p!);
        return result;
    }

    private PgStatement Update(UpdateStatement statement, ExpressionContext c)
    {
        var spec = statement.UpdateSpecification;
        var ctes = Ctes(statement.WithCtesAndXmlNamespaces, c, out var cteScope);
        var cc = c.With(cteScope);
        var target = TargetOf(spec.Target, cc);
        // UPDATE alias SET … FROM table alias: the written target may be an alias defined in FROM.
        if (spec.FromClause is not null && target.Source is { } written && _model.Relation(written) is null)
            target = ResolveAliasTarget(spec.Target, spec.FromClause, cc) ?? target;
        if (spec.FromClause is { } outerFrom && IsOuterJoinTarget(target, outerFrom))
            return OuterJoinUpdate(spec, target, outerFrom, ctes, cc);
        var (alias, from, joins, scope) = TargetAndFrom(target, spec.FromClause, cc);
        var uc = cc.With(scope);
        var set = spec.SetClauses.Select(s => Assignment(s, target, uc)).ToList();
        var condition = spec.WhereClause?.SearchCondition is { } w ? _x.Boolean(w, uc) : null;
        var where = spec.WhereClause?.Cursor is { } cursor
            ? CurrentOfCursor(spec.WhereClause, cursor, joins)
            : spec.TopRowFilter is { } top
                ? And([joins, TopRows(top, target, alias, from, And([joins, condition]), uc, "UPDATE")])
                : And([joins, condition]);
        string qualifier = alias ?? target.Name.Name;
        var update = new PgUpdate(target.Name, alias, set)
        {
            With = ctes, From = from, Where = where,
            Returning = Returning(spec.OutputClause, spec.OutputIntoClause, target, uc, scope, Dml.Update, qualifier, out bool oldRow),
        };
        return OutputInto(oldRow ? JoinOldRow(update, target, qualifier, uc) : update, spec.OutputIntoClause, cc);
    }

    /// <summary>When the UPDATE/DELETE target is an alias, the table it stands for in FROM.</summary>
    private DmlTarget? ResolveAliasTarget(TableReference target, FromClause from, ExpressionContext c)
    {
        if (target is not NamedTableReference { SchemaObject.SchemaIdentifier: null } t)
            return null;
        string alias = t.SchemaObject.BaseIdentifier.Value;
        var finder = new AliasedTable(alias);
        from.Accept(finder);
        return finder.Table is { } table ? TargetOf(table, c, alias) : null;
    }

    private sealed class AliasedTable(string alias) : TSqlFragmentVisitor
    {
        public NamedTableReference? Table { get; private set; }

        public override void Visit(NamedTableReference node)
        {
            if (string.Equals(node.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase))
                Table ??= node;
        }
    }

    private PgAssignment Assignment(SetClause set, DmlTarget target, ExpressionContext c)
    {
        if (set is FunctionCallSetClause { MutatorFunction.FunctionName.Value: var mutator } && mutator.Equals("modify", StringComparison.OrdinalIgnoreCase))
            throw ExpressionTranslator.Untranslatable(set, "the xml modify() method (PostgreSQL's xml type has no update functions: build the new document instead)", DiagnosticCodes.XmlModify);
        if (set is not AssignmentSetClause a || a.Column is null)
            throw ExpressionTranslator.Untranslatable(set, "this SET clause");
        if (a.Variable is not null)
            throw ExpressionTranslator.Untranslatable(set, "SET @v = column = value", DiagnosticCodes.QuirkyUpdate);
        string column = a.Column.MultiPartIdentifier.Identifiers[^1].Value;
        var type = target.Relation.Column(column)?.Type;
        PgExpr value = Assigned(a.NewValue, type, c);
        string? op = a.AssignmentKind switch
        {
            AssignmentKind.Equals => null,
            AssignmentKind.AddEquals => TypeRules.IsString(type) ? "||" : "+",
            AssignmentKind.SubtractEquals => "-",
            AssignmentKind.MultiplyEquals => "*",
            AssignmentKind.DivideEquals => "/",
            AssignmentKind.ModEquals => "%",
            AssignmentKind.BitwiseAndEquals => "&",
            AssignmentKind.BitwiseOrEquals => "|",
            AssignmentKind.BitwiseXorEquals => "#",
            _ => throw ExpressionTranslator.Untranslatable(set, "this compound assignment"),
        };
        string mapped = Column(target, column);
        return new PgAssignment(mapped, op is null ? value : new PgBinary(op, new PgColumnRef(mapped), value));
    }

    private PgStatement Delete(DeleteStatement statement, ExpressionContext c)
    {
        var spec = statement.DeleteSpecification;
        var ctes = Ctes(statement.WithCtesAndXmlNamespaces, c, out var cteScope);
        var cc = c.With(cteScope);
        var target = TargetOf(spec.Target, cc);
        if (spec.FromClause is not null && target.Source is { } written && _model.Relation(written) is null)
            target = ResolveAliasTarget(spec.Target, spec.FromClause, cc) ?? target;
        if (spec.FromClause is { } outerFrom && IsOuterJoinTarget(target, outerFrom))
        {
            var (keyAlias, keys, kc) = OuterJoinKeys(target, outerFrom, spec.WhereClause, spec.TopRowFilter, cc, "DELETE");
            return OutputInto(new PgDelete(target.Name, keyAlias)
            {
                With = ctes, Where = keys,
                Returning = Returning(spec.OutputClause, spec.OutputIntoClause, target, kc, kc.Scope, Dml.Delete, keyAlias, out _),
            }, spec.OutputIntoClause, cc);
        }
        var (alias, from, joins, scope) = TargetAndFrom(target, spec.FromClause, cc);
        var dc = cc.With(scope);
        var condition = spec.WhereClause?.SearchCondition is { } w ? _x.Boolean(w, dc) : null;
        return OutputInto(new PgDelete(target.Name, alias)
        {
            With = ctes, Using = from,
            Where = spec.WhereClause?.Cursor is { } cursor
                ? CurrentOfCursor(spec.WhereClause, cursor, joins)
                : spec.TopRowFilter is { } top
                    ? And([joins, TopRows(top, target, alias, from, And([joins, condition]), dc, "DELETE")])
                    : And([joins, condition]),
            Returning = Returning(spec.OutputClause, spec.OutputIntoClause, target, dc, scope, Dml.Delete, alias ?? target.Name.Name, out _),
        }, spec.OutputIntoClause, cc);
    }

    // ---- TOP and outer-join targets: the rows picked by their ctid ----

    /// <summary>
    /// UPDATE/DELETE TOP (n): <c>ctid IN (SELECT ctid … LIMIT n)</c>, the rows picked first and then
    /// changed. SQL Server changes any n qualifying rows, so which n is arbitrary in both engines.
    /// </summary>
    private PgExpr TopRows(TopRowFilter top, DmlTarget target, string? alias, IReadOnlyList<PgFromItem> from, PgExpr? where, ExpressionContext c, string what)
    {
        if (top.Percent || top.WithTies)
            throw ExpressionTranslator.Untranslatable(top, $"{what} TOP … {(top.Percent ? "PERCENT" : "WITH TIES")}", DiagnosticCodes.DmlTop);
        c.Report(DiagnosticCodes.DmlTop, Severity.Info,
            $"{what} TOP (n) changes the first n rows a key query picks (by ctid): as in SQL Server, which n rows is not defined. Add ORDER BY to a CTE to choose them.");
        string qualifier = alias ?? target.Name.Name;
        var keys = new PgSelect
        {
            Targets = [new PgTarget(new PgColumnRef("ctid", qualifier))],
            From = [new PgTableRef(target.Name, alias), .. from],
            Where = where,
            Limit = _x.Scalar(top.Expression, c),
        };
        return new PgInSubquery(new PgColumnRef("ctid", qualifier), keys);
    }

    /// <summary>Whether the UPDATE/DELETE target is one side of an outer join in FROM.</summary>
    private static bool IsOuterJoinTarget(DmlTarget target, FromClause from)
    {
        string written = target.Alias ?? target.Source?.Name ?? target.Name.Name;
        var leaves = new List<TableReference>();
        var conditions = new List<BooleanExpression>();
        foreach (var r in from.TableReferences)
            Flatten(r, leaves, conditions);
        return !leaves.Any(l => l is NamedTableReference n
                   && (string.Equals(n.Alias?.Value, written, StringComparison.OrdinalIgnoreCase)
                       || (n.Alias is null && string.Equals(n.SchemaObject.BaseIdentifier.Value, written, StringComparison.OrdinalIgnoreCase))))
               && ContainsTarget(from, written);
    }

    /// <summary>
    /// The target inside an outer join: the rows to change are picked by ctid from the whole FROM
    /// clause, <c>ctid IN (SELECT target.ctid FROM … LEFT JOIN … WHERE …)</c>. Returns the target's
    /// alias, that condition, and a context where only the target is visible (for SET and OUTPUT).
    /// </summary>
    private (string Alias, PgExpr Keys, ExpressionContext TargetOnly) OuterJoinKeys(DmlTarget target, FromClause from, WhereClause? where,
        TopRowFilter? top, ExpressionContext c, string what)
    {
        if (where?.Cursor is not null)
            throw ExpressionTranslator.Untranslatable(where, $"{what} … WHERE CURRENT OF with the target inside an outer join", DiagnosticCodes.UpdateFromOuterJoin);
        var scope = c.Scope.Child();
        foreach (var r in from.TableReferences)
            _model.BindTableReference(r, scope, c.Schema);
        var fc = c.With(scope);
        string written = target.Alias ?? target.Source?.Name ?? target.Name.Name;
        string alias = _names.Alias(written);
        var keys = new PgSelect
        {
            Targets = [new PgTarget(new PgColumnRef("ctid", alias))],
            From = from.TableReferences.Select(r => FromItem(r, fc, c)).ToList(),
            Where = where?.SearchCondition is { } w ? _x.Boolean(w, fc) : null,
            Limit = top is null ? null : _x.Scalar(top.Expression, fc),
        };
        if (top is { Percent: true } or { WithTies: true })
            throw ExpressionTranslator.Untranslatable(top, $"{what} TOP … PERCENT", DiagnosticCodes.DmlTop);
        c.Report(DiagnosticCodes.UpdateFromOuterJoin, Severity.Info,
            $"The {what} target is inside an outer join: the rows to change are picked by ctid from the whole FROM clause, then changed.");
        var targetOnly = c.Scope.Child();
        targetOnly.Bind(new Binding(written, target.Relation));
        return (alias, new PgInSubquery(new PgColumnRef("ctid", alias), keys), c.With(targetOnly));
    }

    /// <summary>UPDATE with its target inside an outer join, when SET reads only the target's own columns.</summary>
    private PgStatement OuterJoinUpdate(UpdateSpecification spec, DmlTarget target, FromClause from, IReadOnlyList<PgCte> ctes, ExpressionContext c)
    {
        var (alias, keys, tc) = OuterJoinKeys(target, from, spec.WhereClause, spec.TopRowFilter, c, "UPDATE");
        var finder = new ColumnFinder();
        foreach (var s in spec.SetClauses)
            s.Accept(finder);
        if (finder.Columns.Any(col => tc.Scope.ResolveColumn(col.MultiPartIdentifier.Identifiers.Select(i => i.Value).ToList()) is null))
            throw ExpressionTranslator.Untranslatable(from, "UPDATE whose target is inside an outer join and whose SET reads the other tables", DiagnosticCodes.UpdateFromOuterJoin);
        var set = spec.SetClauses.Select(s => Assignment(s, target, tc)).ToList();
        var update = new PgUpdate(target.Name, alias, set)
        {
            With = ctes, Where = keys,
            Returning = Returning(spec.OutputClause, spec.OutputIntoClause, target, tc, tc.Scope, Dml.Update, alias, out bool oldRow),
        };
        return OutputInto(oldRow ? JoinOldRow(update, target, alias, tc) : update, spec.OutputIntoClause, c);
    }

    private sealed class ColumnFinder : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> Columns { get; } = [];

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Regular && node.MultiPartIdentifier is not null)
                Columns.Add(node);
        }

        // A subquery has its own FROM; its columns are not the SET's.
        public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }

    // ---- MERGE ----

    private PgStatement Merge(MergeStatement statement, ExpressionContext c)
    {
        var spec = statement.MergeSpecification;
        if (spec.TopRowFilter is not null)
            throw ExpressionTranslator.Untranslatable(spec.TopRowFilter, "MERGE TOP (n)", DiagnosticCodes.DmlTop);
        var ctes = Ctes(statement.WithCtesAndXmlNamespaces, c, out var cteScope);
        var cc = c.With(cteScope);
        var target = TargetOf(spec.Target, cc, spec.TableAlias?.Value);
        var scope = cc.Scope.Child();
        scope.Bind(new Binding(target.Alias ?? target.Source?.Name ?? target.Name.Name, target.Relation));
        _model.BindTableReference(spec.TableReference, scope, cc.Schema);
        var mc = cc.With(scope);
        var source = FromItem(spec.TableReference, mc, cc);
        var whens = spec.ActionClauses.Select(a => new PgMergeWhen(
            a.Condition switch
            {
                MergeCondition.Matched => "MATCHED",
                MergeCondition.NotMatchedBySource => "NOT MATCHED BY SOURCE",
                _ => "NOT MATCHED",
            },
            a.SearchCondition is null ? null : _x.Boolean(a.SearchCondition, mc),
            a.Action switch
            {
                UpdateMergeAction u => new PgMergeUpdate(u.SetClauses.Select(s => Assignment(s, target, mc)).ToList()),
                DeleteMergeAction => new PgMergeDelete(),
                InsertMergeAction ins => MergeInsert(ins, target, mc),
                var other => throw ExpressionTranslator.Untranslatable(other, "this MERGE action"),
            })).ToList();
        var merge = new PgMerge(target.Name, target.Alias is null ? null : _names.Alias(target.Alias), source, _x.Boolean(spec.SearchCondition, mc), whens)
        {
            With = ctes,
            Returning = Returning(spec.OutputClause, spec.OutputIntoClause, target, mc, scope, Dml.Merge,
                target.Alias is null ? target.Name.Name : _names.Alias(target.Alias), out _, spec.ActionClauses.Any(a => a.Action is UpdateMergeAction)),
        };
        return OutputInto(_x.Options.TargetVersion < 17 && whens.Any(w => w.Condition == BySource) ? MergeBySource(merge, mc) : merge, spec.OutputIntoClause, cc);
    }

    private const string BySource = "NOT MATCHED BY SOURCE";

    /// <summary>
    /// MERGE … WHEN NOT MATCHED BY SOURCE before PostgreSQL 17, which has no such clause. Each
    /// by-source action becomes a data-modifying CTE on the MERGE: <c>WITH sql2pg_by_source_1 AS
    /// (DELETE FROM t WHERE NOT EXISTS (SELECT 1 FROM source WHERE on) AND condition) MERGE …</c>,
    /// a later clause skipping the rows an earlier one's condition took. Exact, because those rows
    /// match no source row, so the MERGE (which sees the same snapshot) leaves them alone. With
    /// only by-source clauses, the last one is the statement.
    /// </summary>
    private PgStatement MergeBySource(PgMerge merge, ExpressionContext c)
    {
        var unmatched = new PgExists(new PgSelect { Targets = [new PgTarget(new PgLiteral("1"))], From = [merge.Source], Where = merge.On }, Negated: true);
        var actions = new List<PgStatement>();
        PgExpr? taken = null;
        foreach (var w in merge.Whens.Where(w => w.Condition == BySource))
        {
            PgExpr where = unmatched;
            if (taken is not null)
                where = new PgBinary("AND", where, new PgUnary("NOT", new PgFunctionCall("COALESCE", [taken, PgLiteral.False])));
            if (w.And is not null)
                where = new PgBinary("AND", where, w.And);
            actions.Add(w.Action switch
            {
                PgMergeUpdate u => new PgUpdate(merge.Table, merge.Alias, u.Set) { Where = where },
                PgMergeDelete => new PgDelete(merge.Table, merge.Alias) { Where = where },
                var other => throw new UntranslatableException(DiagnosticCodes.MergeBySource, $"WHEN NOT MATCHED BY SOURCE THEN {other} cannot be translated."),
            });
            if (w.And is null)
                break; // later by-source clauses never apply
            taken = taken is null ? w.And : new PgBinary("OR", taken, w.And);
        }
        c.Report(DiagnosticCodes.MergeBySource, Severity.Info,
            $"WHEN NOT MATCHED BY SOURCE (new in PostgreSQL 17) runs as {(actions.Count == 1 ? "an UPDATE or DELETE" : "UPDATEs or DELETEs")} of the target rows no source row matches, in a CTE of the statement for PostgreSQL {_x.Options.TargetVersion}; @@ROWCOUNT counts only the MERGE's own rows.");
        var remaining = merge.Whens.Where(w => w.Condition != BySource).ToList();
        if (remaining.Count == 0)
        {
            var last = actions[^1];
            var before = Ctes(actions.Take(actions.Count - 1));
            return last switch
            {
                PgUpdate u => u with { With = [.. merge.With, .. before] },
                PgDelete d => d with { With = [.. merge.With, .. before] },
                _ => last,
            };
        }
        return merge with { Whens = remaining, With = [.. merge.With, .. Ctes(actions)] };

        static IEnumerable<PgCte> Ctes(IEnumerable<PgStatement> statements) =>
            statements.Select((d, i) => new PgCte($"sql2pg_by_source_{i + 1}", [], new PgModifyingQuery(d)));
    }

    private PgMergeAction MergeInsert(InsertMergeAction ins, DmlTarget target, ExpressionContext c)
    {
        var columns = ins.Columns.Count > 0 ? ins.Columns.Select(col => col.MultiPartIdentifier.Identifiers[^1].Value).ToList() : InsertableColumns(target);
        if (ins.Source is ValuesInsertSource { IsDefaultValues: true })
            return new PgMergeInsert(columns.Select(col => Column(target, col)).ToList(), null);
        var row = ((ValuesInsertSource)ins.Source).RowValues.Single().ColumnValues;
        return new PgMergeInsert(columns.Select(col => Column(target, col)).ToList(),
            row.Select((value, i) => Assigned(value, i < columns.Count ? target.Relation.Column(columns[i])?.Type : null, c)).ToList());
    }
}
