using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// DDL a routine runs on real tables: load procedures drop and re-create indexes and constraints
/// around a bulk load, switch foreign keys off and on, add columns, drop helper routines. Names
/// are mapped as the schema converter maps them, and definitions a statement does not repeat
/// (an index being rebuilt, a constraint being switched back on) come from the source model.
/// Forms not covered here stay S2P5070.
/// </summary>
public sealed partial class ProceduralTranslator
{
    /// <summary>The translation of DDL on real tables and routines, or null when it is not a form translated here.</summary>
    private List<PgPlStatement>? RoutineDdl(TSqlStatement s, BodyState st) => s switch
    {
        CreateIndexStatement i => CreateIndex(i, st),
        CreateColumnStoreIndexStatement i => UnsupportedIndex(s, i.OnName, i.Name.Value, "columnstore", st),
        CreateXmlIndexStatement i => UnsupportedIndex(s, i.OnName, i.Name.Value, "xml", st),
        CreateSelectiveXmlIndexStatement i => UnsupportedIndex(s, i.OnName, i.Name.Value, "selective xml", st),
        CreateFullTextIndexStatement i => UnsupportedIndex(s, i.OnName, "", "full-text", st),
        DropIndexStatement d => DropIndex(d, st),
        AlterIndexStatement a => AlterIndex(a, st),
        AlterTableConstraintModificationStatement m => SwitchConstraint(m, st),
        AlterTableAddTableElementStatement a => AddTableElements(a, st),
        AlterTableDropTableElementStatement d => DropTableElements(d, st),
        AlterTableAlterColumnStatement a => AlterColumn(a, st),
        DropProcedureStatement or DropFunctionStatement => DropRoutines((DropObjectsStatement)s, st),
        _ => null,
    };

    /// <summary>A table a DDL statement names: its source name, PostgreSQL name and model (null when the model does not have it).</summary>
    private (ObjectName Name, PgName Pg, Table? Model) RealTable(SchemaObjectName written, BodyState st)
    {
        var name = _model.ResolveRelation(written, st.C.Schema)
                   ?? new ObjectName(written.SchemaIdentifier?.Value ?? st.C.Schema ?? "dbo", written.BaseIdentifier.Value);
        return (name, _names.Relation(name), _model.Table(name));
    }

    /// <summary>A context where the table's columns resolve unqualified (index filters, CHECK conditions).</summary>
    private ExpressionContext InTable(ObjectName table, BodyState st) =>
        st.C.With(TableScope(table.Name, _model.Relation(table) ?? new RelationInfo(table, ObjectKind.Table, []), st.C));

    private static string Key(ObjectName table, string name) => $"{table}.{name}";

    private static string Last(MultiPartIdentifier id) => id.Identifiers[^1].Value;

    // ---- indexes ----

    private List<PgPlStatement> CreateIndex(CreateIndexStatement index, BodyState st)
    {
        var (table, pg, _) = RealTable(index.OnName, st);
        string name = _names.Index(table, index.Name.Value);
        var notes = new List<string>();
        var result = new List<PgPlStatement>();
        if (index.IndexOptions.OfType<IndexStateOption>().Any(o => o.OptionKind == IndexOptionKind.DropExisting && o.OptionState == OptionState.On))
            result.Add(new PgSqlStatement(new PgDropIndex(new PgName(pg.Schema, name), IfExists: true)));
        if (index.Clustered == true)
            notes.Add("CLUSTERED is dropped: PostgreSQL does not keep rows in index order");
        if (index.OnFileGroupOrPartitionScheme is not null)
            notes.Add("the filegroup or partition scheme is dropped");
        if (index.IndexOptions.Any(o => o is not IndexStateOption { OptionKind: IndexOptionKind.DropExisting }))
            notes.Add("its WITH options are dropped");
        PgExpr? where = index.FilterPredicate is null ? null : _x.Boolean(index.FilterPredicate, InTable(table, st));
        result.Add(new PgSqlStatement(new PgCreateIndex(name, pg,
            index.Columns.Select(k => new PgIndexKey(_names.Column(table, Last(k.Column.MultiPartIdentifier)), k.SortOrder == SortOrder.Descending)).ToList(),
            index.IncludeColumns.Select(k => _names.Column(table, Last(k.MultiPartIdentifier))).ToList(), index.Unique, where)));
        st.SkippedIndexes.Remove(Key(table, index.Name.Value));
        st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info,
            $"CREATE INDEX {index.Name.Value} ON {table} runs in the routine{(notes.Count > 0 ? ": " + string.Join("; ", notes) : "")}.");
        return result;
    }

    /// <summary>An index type PostgreSQL has no counterpart for: not created, as in the converted schema.</summary>
    private List<PgPlStatement> UnsupportedIndex(TSqlStatement s, SchemaObjectName on, string name, string type, BodyState st)
    {
        var (table, _, _) = RealTable(on, st);
        st.SkippedIndexes.Add(Key(table, name));
        return [Untranslated(s, DiagnosticCodes.IndexTypeUnsupported,
            $"The {type} index{(name.Length > 0 ? " " + name : "")} on {table} has no PostgreSQL equivalent and is not created; later DDL on it is dropped.", st, Severity.Warning)];
    }

    /// <summary>An index that does not exist in PostgreSQL: a type the schema converter skips, or one whose CREATE was not translated.</summary>
    private bool IndexAbsent(ObjectName table, Table? model, string name, BodyState st) =>
        st.SkippedIndexes.Contains(Key(table, name))
        || model?.Indexes.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } ix
           && !(ix.Type == "rowstore" || (ix.Type == "spatial" && _x.Options.AllowsExtension(PermittedExtensions.PostGis)));

    private List<PgPlStatement> DropIndex(DropIndexStatement drop, BodyState st)
    {
        var result = new List<PgPlStatement>();
        foreach (var clause in drop.DropIndexClauses)
        {
            (SchemaObjectName on, string index) = clause switch
            {
                DropIndexClause c => (c.Object, c.Index.Value),
                BackwardsCompatibleDropIndexClause b => (TableOf(b.Index), b.Index.ChildIdentifier.Value),
                _ => throw ExpressionTranslator.Untranslatable(clause, "this DROP INDEX", DiagnosticCodes.DdlInRoutine),
            };
            var (table, pg, model) = RealTable(on, st);
            if (IndexAbsent(table, model, index, st))
            {
                st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info, $"DROP INDEX {index} ON {table} is dropped: that index is not created in PostgreSQL.");
                continue;
            }
            result.Add(new PgSqlStatement(new PgDropIndex(new PgName(pg.Schema, _names.Index(table, index)), drop.IsIfExists)));
        }
        if (result.Count > 0)
            st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info, "DROP INDEX runs in the routine; PostgreSQL index names are per schema, so it names the schema.");
        return result;
    }

    /// <summary>The table of <c>DROP INDEX table.index</c> (the old form).</summary>
    private static SchemaObjectName TableOf(ChildObjectName index)
    {
        var table = new SchemaObjectName();
        foreach (var id in index.Identifiers.Take(index.Identifiers.Count - 1))
            table.Identifiers.Add(id);
        return table;
    }

    /// <summary>
    /// ALTER INDEX: REBUILD re-creates the index from its definition when it is missing (disabled
    /// earlier, here or in another routine) and reindexes it otherwise; REORGANIZE reindexes;
    /// DISABLE drops it. <c>ALTER INDEX ALL</c> reindexes the table.
    /// </summary>
    private List<PgPlStatement>? AlterIndex(AlterIndexStatement a, BodyState st)
    {
        var (table, pg, model) = RealTable(a.OnName, st);
        bool rebuild = a.AlterIndexType is AlterIndexType.Rebuild or AlterIndexType.Reorganize;
        if (a.All)
        {
            if (!rebuild)
                return null;
            st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info, $"ALTER INDEX ALL ON {table} {a.AlterIndexType.ToString().ToUpperInvariant()} becomes REINDEX TABLE.");
            return [new PgSqlStatement(new PgReindex(pg, Table: true))];
        }
        string source = a.Name.Value;
        if (IndexAbsent(table, model, source, st))
        {
            st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info, $"ALTER INDEX {source} ON {table} is dropped: that index is not created in PostgreSQL.");
            return [];
        }
        var index = new PgName(pg.Schema, _names.Index(table, source));
        var definition = model?.Indexes.FirstOrDefault(i => i.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
        switch (a.AlterIndexType)
        {
            case AlterIndexType.Rebuild when definition is not null:
                st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info,
                    $"ALTER INDEX {source} ON {table} REBUILD creates the index from its definition when it is missing (disabled), and reindexes it otherwise.");
                return [new PgIf([new PgIfBranch(new PgTemplate("to_regclass({0}) IS NULL", [PgLiteral.String(index.Qualified)]),
                        [new PgSqlStatement(ModelIndex(table, pg, definition, st))])],
                    [new PgSqlStatement(new PgReindex(index))])];
            case AlterIndexType.Rebuild or AlterIndexType.Reorganize:
                st.C.Report(DiagnosticCodes.IndexDdlInRoutine, Severity.Info,
                    $"ALTER INDEX {source} ON {table} {a.AlterIndexType.ToString().ToUpperInvariant()} becomes REINDEX{(a.AlterIndexType == AlterIndexType.Reorganize ? ", which rebuilds the whole index (PostgreSQL has no lighter form)" : "")}.");
                return [new PgSqlStatement(new PgReindex(index))];
            case AlterIndexType.Disable when definition is not null:
                st.C.Report(DiagnosticCodes.IndexDisabled, Severity.Warning,
                    $"ALTER INDEX {source} ON {table} DISABLE drops the index: PostgreSQL cannot disable one. ALTER INDEX … REBUILD creates it again.");
                return [new PgSqlStatement(new PgDropIndex(index, IfExists: true))];
            default:
                return null;
        }
    }

    /// <summary>CREATE INDEX from the source model's definition.</summary>
    private PgCreateIndex ModelIndex(ObjectName table, PgName pg, TableIndex ix, BodyState st)
    {
        PgExpr? where = ix.FilterExpression is { } filter ? _x.Boolean(ParseBoolean(filter, st), InTable(table, st)) : null;
        return new PgCreateIndex(_names.Index(table, ix.Name), pg, ix.Keys.Select(k => new PgIndexKey(_names.Column(table, k.Name), k.Descending)).ToList(),
            ix.Included.Select(c => _names.Column(table, c)).ToList(), ix.IsUnique, where,
            ix.Type == "spatial" ? "gist" : null);
    }

    private static BooleanExpression ParseBoolean(string tsql, BodyState st)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var e = parser.ParseBooleanExpression(new StringReader(tsql), out var errors);
        return errors.Count == 0 && e is not null
            ? e
            : throw new UntranslatableException(DiagnosticCodes.DdlInRoutine, $"The definition '{tsql}' in the source model does not parse.");
    }

    // ---- constraints ----

    /// <summary>
    /// NOCHECK CONSTRAINT drops the constraint (PostgreSQL cannot switch one off); CHECK CONSTRAINT
    /// adds it back NOT VALID when it is missing (SQL Server does not check existing rows either),
    /// and validates it under WITH CHECK. Both work whichever routine ran the other.
    /// </summary>
    private List<PgPlStatement>? SwitchConstraint(AlterTableConstraintModificationStatement m, BodyState st)
    {
        var (table, pg, model) = RealTable(m.SchemaObjectName, st);
        IEnumerable<string> sources;
        if (m.All)
        {
            if (model is null)
                return null;
            sources = model.ForeignKeys.Select(f => f.Name).Concat(model.Checks.Select(c => c.Name)).OfType<string>();
        }
        else
            sources = m.ConstraintNames.Select(n => n.Value);

        var result = new List<PgPlStatement>();
        foreach (string source in sources)
        {
            string name = _names.Constraint(table, source);
            var add = st.AddedConstraints.GetValueOrDefault(Key(table, name)) ?? ModelConstraint(table, pg, model, source, st);
            if (add is null)
                return null;
            if (m.ConstraintEnforcement == Microsoft.SqlServer.TransactSql.ScriptDom.ConstraintEnforcement.NoCheck)
            {
                result.Add(new PgSqlStatement(new PgDropConstraint(pg, name)));
                continue;
            }
            result.Add(new PgIf([new PgIfBranch(
                new PgTemplate("NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = to_regclass({0}) AND conname = {1})",
                    [PgLiteral.String(pg.Qualified), PgLiteral.String(name)]),
                [new PgSqlStatement(add)])]));
            if (m.ExistingRowsCheckEnforcement == Microsoft.SqlServer.TransactSql.ScriptDom.ConstraintEnforcement.Check)
                result.Add(new PgSqlStatement(new PgValidateConstraint(pg, name)));
        }
        st.C.Report(DiagnosticCodes.ConstraintDisabled, Severity.Warning, m.ConstraintEnforcement == Microsoft.SqlServer.TransactSql.ScriptDom.ConstraintEnforcement.NoCheck
            ? $"NOCHECK CONSTRAINT on {table} drops the constraint: PostgreSQL cannot switch one off. CHECK CONSTRAINT adds it back."
            : $"CHECK CONSTRAINT on {table} adds the constraint back NOT VALID when NOCHECK dropped it{(m.ExistingRowsCheckEnforcement == Microsoft.SqlServer.TransactSql.ScriptDom.ConstraintEnforcement.Check ? ", then validates the existing rows" : "")}.");
        return result;
    }

    /// <summary>A foreign key or check from the source model, as ADD CONSTRAINT … NOT VALID.</summary>
    private PgStatement? ModelConstraint(ObjectName table, PgName pg, Table? model, string source, BodyState st)
    {
        string name = _names.Constraint(table, source);
        if (model?.ForeignKeys.FirstOrDefault(f => source.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) is { } fk)
            return new PgAddForeignKey(pg, name, fk.Columns.Select(c => _names.Column(table, c)).ToList(), _names.Relation(fk.Referenced),
                fk.ReferencedColumns.Select(c => _names.Column(fk.Referenced, c)).ToList(), Action(fk.OnDelete), Action(fk.OnUpdate), NotValid: true);
        if (model?.Checks.FirstOrDefault(c => source.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is { } check)
            return new PgAddCheck(pg, name, _x.Boolean(ParseBoolean(check.Expression, st), InTable(table, st)), NotValid: true);
        return null;
    }

    private static string? Action(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => null,
    };

    private static string? Action(DeleteUpdateAction action) => action switch
    {
        DeleteUpdateAction.Cascade => "CASCADE",
        DeleteUpdateAction.SetNull => "SET NULL",
        DeleteUpdateAction.SetDefault => "SET DEFAULT",
        _ => null,
    };

    /// <summary>ALTER TABLE … ADD columns and constraints (keys, foreign keys, checks, defaults).</summary>
    private List<PgPlStatement>? AddTableElements(AlterTableAddTableElementStatement a, BodyState st)
    {
        var definition = a.Definition;
        if (definition.SystemTimePeriod is not null || definition.Indexes.Count > 0)
            return null;
        var (table, pg, model) = RealTable(a.SchemaObjectName, st);
        bool notValid = a.ExistingRowsCheckEnforcement == Microsoft.SqlServer.TransactSql.ScriptDom.ConstraintEnforcement.NoCheck;
        var result = new List<PgPlStatement>();
        foreach (var col in definition.ColumnDefinitions)
        {
            if (AddColumn(table, pg, col, st) is not { } add)
                return null;
            result.Add(new PgSqlStatement(add));
        }
        var added = new List<(string Key, PgStatement Statement, bool IsKey)>();
        foreach (var constraint in definition.TableConstraints)
        {
            if (constraint.ConstraintIdentifier is not { Value: var source })
                return null;
            PgStatement? x;
            bool isKey = false;
            switch (constraint)
            {
                case UniqueConstraintDefinition u:
                    var columns = u.Columns.Select(k => _names.Column(table, Last(k.Column.MultiPartIdentifier))).ToList();
                    x = u.IsPrimaryKey ? new PgAddPrimaryKey(pg, _names.Index(table, source), columns) : new PgAddUnique(pg, _names.Index(table, source), columns);
                    isKey = true;
                    break;
                case ForeignKeyConstraintDefinition f:
                    var referenced = _model.ResolveRelation(f.ReferenceTableName, st.C.Schema)
                                     ?? new ObjectName(f.ReferenceTableName.SchemaIdentifier?.Value ?? st.C.Schema ?? "dbo", f.ReferenceTableName.BaseIdentifier.Value);
                    var referencedColumns = f.ReferencedTableColumns.Count > 0
                        ? f.ReferencedTableColumns.Select(c => c.Value).ToList()
                        : _model.Table(referenced)?.PrimaryKey?.Columns.Select(c => c.Name).ToList();
                    if (referencedColumns is null)
                        return null;
                    x = new PgAddForeignKey(pg, _names.Constraint(table, source), f.Columns.Select(c => _names.Column(table, c.Value)).ToList(),
                        _names.Relation(referenced), referencedColumns.Select(c => _names.Column(referenced, c)).ToList(),
                        Action(f.DeleteAction), Action(f.UpdateAction), notValid);
                    break;
                case CheckConstraintDefinition c:
                    x = new PgAddCheck(pg, _names.Constraint(table, source), _x.Boolean(c.CheckCondition, InTable(table, st)), notValid);
                    break;
                case DefaultConstraintDefinition d when d.Column is not null:
                    var column = model?.Columns.FirstOrDefault(col => col.Name.Equals(d.Column.Value, StringComparison.OrdinalIgnoreCase));
                    x = new PgSetDefault(pg, _names.Column(table, d.Column.Value), Assigned(d.Expression, column?.Type, null, InTable(table, st)));
                    break;
                default:
                    return null;
            }
            result.Add(new PgSqlStatement(x));
            added.Add((Key(table, x is PgAddForeignKey or PgAddCheck ? _names.Constraint(table, source) : source), x, isKey));
        }
        foreach (var (key, x, isKey) in added)
        {
            if (isKey)
                st.AddedKeys.Add(key);
            else if (x is PgAddForeignKey fk)
                st.AddedConstraints[key] = fk with { NotValid = true };
            else if (x is PgAddCheck ck)
                st.AddedConstraints[key] = ck with { NotValid = true };
        }
        st.C.Report(DiagnosticCodes.SchemaChangeInRoutine, Severity.Warning,
            $"The routine adds {string.Join(", ", definition.ColumnDefinitions.Select(c => "column " + c.ColumnIdentifier.Value).Concat(definition.TableConstraints.Select(c => "constraint " + c.ConstraintIdentifier!.Value)))} to {table} at run time; the converted schema does not have {(definition.ColumnDefinitions.Count + definition.TableConstraints.Count == 1 ? "it" : "them")} until the routine runs.");
        return result;
    }

    /// <summary>A column added by ALTER TABLE … ADD: type, NOT NULL, default and identity (not computed or with inline constraints).</summary>
    private PgAddColumn? AddColumn(ObjectName table, PgName pg, ColumnDefinition col, BodyState st)
    {
        if (col.ComputedColumnExpression is not null || col.DataType is null || col.Constraints.Any(c => c is not NullableConstraintDefinition))
            return null;
        var type = _model.TypeOf(col.DataType, st.C.Schema);
        var mapped = _x.Types.Map(type with { UserTypeName = null }, st.C.Owner);
        foreach (var diagnostic in mapped.Diagnostics.Where(x => x.Severity != Severity.Info))
            st.C.Diagnostics.Add(diagnostic with { Object = st.C.Owner });
        if (mapped.Extension is not null)
            st.C.Extensions.Add(mapped.Extension);
        PgIdentity? identity = col.IdentityOptions is { } id ? new PgIdentity(Seed(id.IdentitySeed), Seed(id.IdentityIncrement)) : null;
        string pgType = identity is not null && mapped.PgType.StartsWith("numeric", StringComparison.Ordinal) ? "bigint" : mapped.PgType;
        PgExpr? defaultValue = col.DefaultConstraint?.Expression is { } d ? Assigned(d, type, col.DataType, st.C) : null;
        bool notNull = col.Constraints.OfType<NullableConstraintDefinition>().Any(n => !n.Nullable);
        return new PgAddColumn(pg, new PgColumnDef(_names.Column(table, col.ColumnIdentifier.Value), pgType, notNull, defaultValue, identity));
    }

    /// <summary>ALTER TABLE … DROP COLUMN or DROP CONSTRAINT (a default constraint drops the column's default).</summary>
    private List<PgPlStatement>? DropTableElements(AlterTableDropTableElementStatement d, BodyState st)
    {
        // DROP PERIOD FOR SYSTEM_TIME has no name (a temporal table, WP 4.2).
        if (d.AlterTableDropTableElements.Any(e => e.Name is null || e.TableElementType is TableElementType.Period or TableElementType.Index))
            return null;
        var (table, pg, model) = RealTable(d.SchemaObjectName, st);
        var result = new List<PgPlStatement>();
        var kinds = new List<TableElementType>();
        // An element without COLUMN or CONSTRAINT is the same kind as the one before it (a constraint when first).
        var kind = TableElementType.Constraint;
        foreach (var e in d.AlterTableDropTableElements)
        {
            string source = e.Name.Value;
            if (e.TableElementType != TableElementType.NotSpecified)
                kind = e.TableElementType;
            kinds.Add(kind);
            switch (kind)
            {
                case TableElementType.Column:
                    result.Add(new PgSqlStatement(new PgDropColumn(pg, _names.Column(table, source), e.IsIfExists)));
                    break;
                case TableElementType.Constraint:
                    if (model?.Columns.FirstOrDefault(c => source.Equals(c.DefaultConstraintName, StringComparison.OrdinalIgnoreCase)) is { } column)
                        result.Add(new PgSqlStatement(new PgSetDefault(pg, _names.Column(table, column.Name), null)));
                    else if (IsKey(table, model, source, st))
                        result.Add(new PgSqlStatement(new PgDropConstraint(pg, _names.Index(table, source), e.IsIfExists)));
                    else
                        result.Add(new PgSqlStatement(new PgDropConstraint(pg, _names.Constraint(table, source), e.IsIfExists)));
                    break;
                default:
                    return null;
            }
        }
        st.C.Report(DiagnosticCodes.SchemaChangeInRoutine, Severity.Warning,
            $"The routine drops {string.Join(", ", d.AlterTableDropTableElements.Select((e, i) => (kinds[i] == TableElementType.Column ? "column " : "constraint ") + e.Name.Value))} from {table} at run time.");
        return result;
    }

    /// <summary>A primary key or unique constraint: PostgreSQL names it as an index (unique per schema).</summary>
    private static bool IsKey(ObjectName table, Table? model, string source, BodyState st) =>
        st.AddedKeys.Contains(Key(table, source))
        || source.Equals(model?.PrimaryKey?.Name, StringComparison.OrdinalIgnoreCase)
        || model?.Uniques.Any(u => source.Equals(u.Name, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>ALTER TABLE … ALTER COLUMN c type [NULL | NOT NULL].</summary>
    private List<PgPlStatement>? AlterColumn(AlterTableAlterColumnStatement a, BodyState st)
    {
        if (a.DataType is null || a.AlterTableAlterColumnOption is not (AlterTableAlterColumnOption.NoOptionDefined
                or AlterTableAlterColumnOption.Null or AlterTableAlterColumnOption.NotNull))
            return null;
        var (table, pg, _) = RealTable(a.SchemaObjectName, st);
        var type = _model.TypeOf(a.DataType, st.C.Schema);
        var mapped = _x.Types.Map(type with { UserTypeName = null }, st.C.Owner);
        if (mapped.Extension is not null)
            st.C.Extensions.Add(mapped.Extension);
        // SQL Server makes the column nullable when ALTER COLUMN does not say NOT NULL.
        bool notNull = a.AlterTableAlterColumnOption == AlterTableAlterColumnOption.NotNull;
        st.C.Report(DiagnosticCodes.SchemaChangeInRoutine, Severity.Warning,
            $"The routine changes column {a.ColumnIdentifier.Value} of {table} to {mapped.PgType} at run time.");
        return [new PgSqlStatement(new PgAlterColumn(pg, _names.Column(table, a.ColumnIdentifier.Value), mapped.PgType, notNull))];
    }

    // ---- routines ----

    /// <summary>DROP PROCEDURE / DROP FUNCTION → DROP ROUTINE, which drops either (a procedure may have become a function).</summary>
    private List<PgPlStatement> DropRoutines(DropObjectsStatement drop, BodyState st)
    {
        var result = new List<PgPlStatement>();
        foreach (var o in drop.Objects)
        {
            var name = _model.ResolveRoutine(o, st.C.Schema) ?? new ObjectName(o.SchemaIdentifier?.Value ?? st.C.Schema ?? "dbo", o.BaseIdentifier.Value);
            result.Add(new PgSqlStatement(new PgDropRoutine(_names.Routine(name), drop.IsIfExists)));
        }
        st.C.Report(DiagnosticCodes.SchemaChangeInRoutine, Severity.Warning,
            $"The routine drops {string.Join(", ", drop.Objects.Select(o => ExpressionTranslator.Text(o)))} at run time (DROP ROUTINE drops a procedure or a function).");
        return result;
    }
}
