using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Temp tables (<c>#t</c>) and table variables (<c>@t</c>), both PostgreSQL temporary tables.
/// A temp table keeps its name without <c>#</c>; a routine drops it first, in case an earlier
/// call in the session left it, and drops it again when it ends or returns, as SQL Server does
/// (not when the routine returns cursors, which read it after the routine has ended). A table
/// variable is <c>tv_&lt;routine&gt;_&lt;name&gt;</c>, created at the start of the body (T-SQL
/// declares it once, however often the DECLARE runs) and dropped when the body ends.
/// </summary>
public sealed partial class ProceduralTranslator
{
    /// <summary>Whether a written name is a temp table (<c>#t</c>; <c>##t</c> too, which is refused where it is used).</summary>
    private static bool IsTemp(SchemaObjectName name) => name.BaseIdentifier.Value.StartsWith('#');

    private static bool IsTempTableDdl(TSqlStatement s) => s switch
    {
        CreateTableStatement create => IsTemp(create.SchemaObjectName),
        DropTableStatement drop => drop.Objects.Count > 0 && drop.Objects.All(IsTemp),
        CreateIndexStatement index => IsTemp(index.OnName),
        _ => false,
    };

    /// <summary>
    /// A temp table or table variable statement of a script, outside a DO block: CREATE TABLE #t,
    /// DROP TABLE #t, CREATE INDEX on #t, and DECLARE @t TABLE (a temporary table recorded in
    /// <paramref name="tableVariables"/>, which the script drops at the end of the batch).
    /// </summary>
    public IReadOnlyList<PgStatement> ScriptTable(TSqlStatement s, ExpressionContext c, IDictionary<string, string> tableVariables)
    {
        var st = new BodyState(BodySignature.AnonymousBlock, c);
        foreach (var (variable, table) in tableVariables)
            st.TableVariables[variable] = table;
        var translated = WithHooks(st, () => Statement(s, null, st).ToList());
        foreach (var (variable, table) in st.TableVariables)
            tableVariables[variable] = table;
        return st.TableVariableSetup.Values.SelectMany(x => x).Concat(translated).Cast<PgSqlStatement>().Select(x => x.Statement).ToList();
    }

    /// <summary>Whether a script statement is one <see cref="ScriptTable"/> translates.</summary>
    public static bool IsTableStatement(TSqlStatement s) => IsTempTableDdl(s) || s is DeclareTableVariableStatement;

    /// <summary>The temporary table a table variable is kept in: <c>tv_name</c> in a script, <c>tv_routine_name</c> in a routine (callers' table variables of the same name stay apart).</summary>
    private string TableVariableName(string variable, BodyState st) =>
        _names.Alias("tv_" + (st.Signature.RoutineName is { } routine ? routine + "_" : "") + variable.TrimStart('@'));

    /// <summary>The temp tables and table variables a body creates, found before it is translated (RETURN drops them all).</summary>
    private sealed class TableFinder : TSqlFragmentVisitor
    {
        public HashSet<string> TempTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TableVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(CreateTableStatement node)
        {
            if (IsTemp(node.SchemaObjectName) && !node.SchemaObjectName.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal))
                TempTables.Add(node.SchemaObjectName.BaseIdentifier.Value);
        }

        public override void Visit(SelectStatement node)
        {
            if (node.Into is { } into && IsTemp(into) && !into.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal))
                TempTables.Add(into.BaseIdentifier.Value);
        }

        public override void Visit(DeclareTableVariableStatement node) => TableVariables.Add(node.Body.VariableName.Value);
    }

    /// <summary>Works out which tables the body drops when it ends or returns.</summary>
    private void PlanDrops(IList<TSqlStatement> statements, BodyState st)
    {
        var finder = new TableFinder();
        foreach (var s in statements)
            s.Accept(finder);
        st.CreatedTempTables.UnionWith(finder.TempTables);
        var sig = st.Signature;
        // A script batch's temp tables live on in the session; its table variables end with it.
        // A procedure's both end with it, unless cursors it returns still read them. A function's
        // table variables go when the transaction commits (ON COMMIT DROP).
        bool procedure = sig.Kind is BodyKind.Procedure or BodyKind.Trigger && sig.Shape is not { Cursors.Count: > 0 };
        if (procedure)
            st.EndDrops.AddRange(finder.TempTables.Select(t => new PgName("", _queries.TempTable(t))));
        if (procedure || sig.Kind == BodyKind.AnonymousBlock)
            st.EndDrops.AddRange(finder.TableVariables.Select(v => new PgName("", TableVariableName(v, st))));
    }

    private IEnumerable<PgPlStatement> Drops(BodyState st) =>
        st.EndDrops.Select(name => (PgPlStatement)new PgSqlStatement(new PgDropTable(name)));

    /// <summary>DECLARE @t TABLE (…): created at the start of the body, whatever the DECLARE's place.</summary>
    private IEnumerable<PgPlStatement> DeclareTable(DeclareTableVariableStatement d, BodyState st)
    {
        string variable = d.Body.VariableName.Value;
        if (st.TableVariables.ContainsKey(variable))
            throw ExpressionTranslator.Untranslatable(d, $"{variable}, declared a second time,", DiagnosticCodes.VariableRedeclared);
        bool function = st.Signature.Kind is BodyKind.ScalarFunction or BodyKind.TableFunction;
        st.TableVariableSetup[variable] = TableDefinition(variable, TableVariableName(variable, st), d.Body.Definition, st,
            onCommit: function ? "DROP" : null, dropFirst: true);
        return [];
    }

    /// <summary>CREATE TABLE #t (…).</summary>
    private List<PgPlStatement> CreateTemp(CreateTableStatement create, BodyState st)
    {
        string written = create.SchemaObjectName.BaseIdentifier.Value;
        QueryTranslator.GlobalTemp(create, written);
        if (create.AsNode || create.AsEdge || create.AsFileTable)
            throw ExpressionTranslator.Untranslatable(create, "a graph or FileTable temp table", DiagnosticCodes.ProceduralStatement);
        // A script's CREATE fails when the table exists, as SQL Server's does; a routine starts afresh.
        return TableDefinition(written, _queries.TempTable(written), create.Definition, st, onCommit: null,
            dropFirst: st.Signature.Kind != BodyKind.AnonymousBlock);
    }

    /// <summary>
    /// A temporary table with the declared columns (types, NOT NULL, defaults, identity), keys,
    /// CHECK constraints and indexes, dropped first when <paramref name="dropFirst"/>. Its columns
    /// are defined in the scope under <paramref name="key"/> (<c>#t</c> or <c>@t</c>).
    /// </summary>
    private List<PgPlStatement> TableDefinition(string key, string table, TableDefinition definition, BodyState st, string? onCommit, bool dropFirst)
    {
        var c = st.C;
        var name = new PgName("", table);
        var columns = new List<PgColumnDef>();
        var relationColumns = new List<ColumnInfo>();
        var after = new List<PgStatement>();
        var checks = new List<BooleanExpression>();
        var indexes = new List<IndexDefinition>();
        int keys = 0;
        foreach (var col in definition.ColumnDefinitions)
        {
            if (col.ComputedColumnExpression is not null || col.DataType is null)
                throw ExpressionTranslator.Untranslatable(col, "a computed column in a temp table or table variable", DiagnosticCodes.ProceduralStatement);
            var type = _model.TypeOf(col.DataType, c.Schema);
            var mapped = _x.Types.Map(type with { UserTypeName = null }, c.Owner);
            foreach (var diagnostic in mapped.Diagnostics.Where(x => x.Severity != Severity.Info))
                c.Diagnostics.Add(diagnostic with { Object = c.Owner });
            if (mapped.Extension is not null)
                c.Extensions.Add(mapped.Extension);
            string column = _names.Alias(col.ColumnIdentifier.Value);
            bool notNull = col.Constraints.OfType<NullableConstraintDefinition>().Any(n => !n.Nullable)
                           || col.Constraints.OfType<UniqueConstraintDefinition>().Any(u => u.IsPrimaryKey);
            PgIdentity? identity = col.IdentityOptions is { } id
                ? new PgIdentity(Seed(id.IdentitySeed), Seed(id.IdentityIncrement))
                : null;
            string pgType = identity is not null && mapped.PgType.StartsWith("numeric", StringComparison.Ordinal) ? "bigint" : mapped.PgType;
            PgExpr? defaultValue = col.DefaultConstraint?.Expression is { } d ? Assigned(d, type, col.DataType, c) : null;
            if (col.Collation is { Value: var collation } && !collation.Equals("database_default", StringComparison.OrdinalIgnoreCase))
                c.Report(DiagnosticCodes.CollationIgnored, Severity.Info,
                    $"COLLATE {collation} on {key}.{col.ColumnIdentifier.Value} is dropped; the column has the database's default collation.");
            columns.Add(new PgColumnDef(column, pgType, notNull, defaultValue, identity));
            relationColumns.Add(new ColumnInfo(col.ColumnIdentifier.Value, type));
            if (identity is not null)
                st.IdentityColumns[key] = column;
            foreach (var constraint in col.Constraints)
            {
                switch (constraint)
                {
                    case UniqueConstraintDefinition u:
                        after.Add(Key(name, table, u.IsPrimaryKey, [column], keys++));
                        break;
                    case CheckConstraintDefinition check:
                        checks.Add(check.CheckCondition);
                        break;
                    case ForeignKeyConstraintDefinition:
                        ForeignKeyDropped(key, c);
                        break;
                }
            }
            if (col.Index is { } index)
                indexes.Add(index);
        }
        foreach (var constraint in definition.TableConstraints)
        {
            switch (constraint)
            {
                case UniqueConstraintDefinition u:
                    after.Add(Key(name, table, u.IsPrimaryKey, u.Columns.Select(k => _names.Alias(k.Column.MultiPartIdentifier.Identifiers[^1].Value)).ToList(), keys++));
                    break;
                case CheckConstraintDefinition check:
                    checks.Add(check.CheckCondition);
                    break;
                case ForeignKeyConstraintDefinition:
                    ForeignKeyDropped(key, c);
                    break;
                default:
                    throw ExpressionTranslator.Untranslatable(constraint, "this constraint on a temp table or table variable", DiagnosticCodes.ProceduralStatement);
            }
        }
        indexes.AddRange(definition.Indexes);

        var relation = new RelationInfo(null, ObjectKind.Table, relationColumns);
        st.TableVariables.Remove(key);
        if (key.StartsWith('@'))
            st.TableVariables[key] = table;
        c.Scope.DefineTable(key, relation);
        var inTable = c.With(TableScope(key, relation, c));
        for (int k = 0; k < checks.Count; k++)
            after.Add(new PgAddCheck(name, $"{table}_check{k + 1}", _x.Boolean(checks[k], inTable)));
        foreach (var index in indexes)
            after.Add(new PgCreateIndex("", name, index.Columns.Select(k => IndexKey(k)).ToList(),
                index.IncludeColumns.Select(k => _names.Alias(k.MultiPartIdentifier.Identifiers[^1].Value)).ToList(),
                index.Unique, index.FilterPredicate is null ? null : _x.Boolean(index.FilterPredicate, inTable)));

        var result = new List<PgPlStatement>();
        if (dropFirst)
            result.Add(new PgSqlStatement(new PgDropTable(name)));
        result.Add(new PgSqlStatement(new PgCreateTable(name, columns) { Temporary = true, OnCommit = onCommit }));
        result.AddRange(after.Select(x => new PgSqlStatement(x)));
        return result;
    }

    private PgIndexKey IndexKey(ColumnWithSortOrder k) =>
        new(_names.Alias(k.Column.MultiPartIdentifier.Identifiers[^1].Value), k.SortOrder == SortOrder.Descending);

    /// <summary>A scope where the table's columns resolve unqualified (CHECK and index filter expressions).</summary>
    private static Scope TableScope(string key, RelationInfo relation, ExpressionContext c)
    {
        var scope = c.Scope.Child();
        scope.Bind(new Binding(key, relation));
        return scope;
    }

    private static void ForeignKeyDropped(string key, ExpressionContext c) =>
        c.Report(DiagnosticCodes.TempTableForeignKey, Severity.Info,
            $"The FOREIGN KEY on {key} is dropped: SQL Server does not enforce foreign keys on temporary tables either.");

    /// <summary>DROP TABLE [IF EXISTS] #t, …</summary>
    private IEnumerable<PgPlStatement> DropTemp(DropTableStatement drop) =>
        drop.Objects.Select(o =>
        {
            QueryTranslator.GlobalTemp(drop, o.BaseIdentifier.Value);
            return (PgPlStatement)new PgSqlStatement(new PgDropTable(new PgName("", _queries.TempTable(o.BaseIdentifier.Value)), drop.IsIfExists));
        }).ToList();

    /// <summary>CREATE INDEX on #t: PostgreSQL names it (index names share the temp schema with the tables).</summary>
    private PgPlStatement CreateTempIndex(CreateIndexStatement index, BodyState st)
    {
        string written = index.OnName.BaseIdentifier.Value;
        QueryTranslator.GlobalTemp(index, written);
        var relation = st.C.Scope.Table(written) ?? new RelationInfo(null, ObjectKind.Table, []);
        var inTable = st.C.With(TableScope(written, relation, st.C));
        return new PgSqlStatement(new PgCreateIndex("", new PgName("", _queries.TempTable(written)), index.Columns.Select(IndexKey).ToList(),
            index.IncludeColumns.Select(k => _names.Alias(k.MultiPartIdentifier.Identifiers[^1].Value)).ToList(),
            index.Unique, index.FilterPredicate is null ? null : _x.Boolean(index.FilterPredicate, inTable)));
    }

    /// <summary>
    /// After a ROLLBACK, the table variables created in the rolled-back transaction are gone in
    /// PostgreSQL (SQL Server keeps them and their rows): they are created again, empty.
    /// </summary>
    private IEnumerable<PgPlStatement> RecreateTableVariables(BodyState st)
    {
        foreach (var (variable, setup) in st.TableVariableSetup)
        {
            string table = st.TableVariables[variable];
            yield return new PgIf([new PgIfBranch(
                new PgTemplate("to_regclass({0}) IS NULL", [PgLiteral.String("pg_temp." + NameMapper.Quote(table))]),
                setup)]);
        }
    }
}
