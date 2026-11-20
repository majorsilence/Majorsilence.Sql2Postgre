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
        AlterTableAddTableElementStatement { Definition.SystemTimePeriod: not null } a when a.Definition.ColumnDefinitions.Count == 0 && a.Definition.TableConstraints.Count == 0
            => TogglePeriod(a.SchemaObjectName, enable: true, st),
        AlterTableAddTableElementStatement a => AddTableElements(a, st),
        AlterTableDropTableElementStatement d when d.AlterTableDropTableElements.All(e => e.TableElementType == TableElementType.Period)
            => TogglePeriod(d.SchemaObjectName, enable: false, st),
        AlterTableDropTableElementStatement d => DropTableElements(d, st),
        AlterTableSetStatement set when set.Options.Count > 0 && set.Options.All(o => o is SystemVersioningTableOption) => SetVersioning(set, st),
        AlterTableAlterColumnStatement a => AlterColumn(a, st),
        DropProcedureStatement or DropFunctionStatement => DropRoutines((DropObjectsStatement)s, st),
        DropTriggerStatement { TriggerScope: TriggerScope.Normal } d => DropTriggers(d, st),
        CreateSecurityPolicyStatement p => CreateSecurityPolicy(p, st),
        DropSecurityPolicyStatement d => DropSecurityPolicies(d, st),
        AlterSequenceStatement a when a.SequenceOptions is [ScalarExpressionSequenceOption { OptionKind: SequenceOptionKind.Restart }]
            or [SequenceOption { OptionKind: SequenceOptionKind.Restart }] => RestartSequence(a, st),
        CreateRoleStatement r => Role(new PgCreateRole(RoleName(r.Name)), st,
            r.Owner is null ? null : $"AUTHORIZATION {r.Owner.Value} is dropped (a PostgreSQL role has no owner)"),
        AlterRoleStatement { Action: AddMemberAlterRoleAction add } r => Role(new PgGrantRole(RoleName(r.Name), RoleName(add.Member), Revoke: false), st),
        AlterRoleStatement { Action: DropMemberAlterRoleAction drop } r => Role(new PgGrantRole(RoleName(r.Name), RoleName(drop.Member), Revoke: true), st),
        AlterRoleStatement { Action: RenameAlterRoleAction rename } r => Role(new PgRenameRole(RoleName(r.Name), RoleName(rename.NewName)), st),
        DropRoleStatement d => Role(new PgDropRole(RoleName(d.Name), d.IsIfExists), st),
        _ => null,
    };

    // ---- roles ----

    /// <summary>
    /// A role name as written: PostgreSQL roles are not schema objects, so the name style does not
    /// apply, and a name dynamic SQL puts in at run time is used as it is.
    /// </summary>
    private string RoleName(Identifier name)
    {
        if (name.Value.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            _verbatimMarkers.Add(name.Value);
        return name.Value;
    }

    private List<PgPlStatement> Role(PgStatement statement, BodyState st, string? note = null)
    {
        st.C.Report(DiagnosticCodes.RoleDdl, Severity.Info,
            "Database roles become PostgreSQL roles, which belong to the server, not to one database: every database there sees them. "
            + "Role names are kept as written (case and all); creating, renaming and dropping roles needs CREATEROLE, and adding members needs ADMIN on the role."
            + (note is null ? "" : $" {char.ToUpperInvariant(note[0])}{note[1..]}."));
        return [new PgSqlStatement(statement)];
    }

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

    // ---- temporal tables ----

    /// <summary>
    /// <c>ADD</c>/<c>DROP PERIOD FOR SYSTEM_TIME</c>: the trigger that sets the period columns is
    /// enabled or disabled (with the period dropped, SQL Server leaves the columns to the code).
    /// </summary>
    private List<PgPlStatement>? TogglePeriod(SchemaObjectName written, bool enable, BodyState st)
    {
        var (table, pg, model) = RealTable(written, st);
        if (model?.Period is null)
            return null;
        st.C.Report(DiagnosticCodes.SystemVersioningInRoutine, Severity.Info,
            $"{(enable ? "ADD" : "DROP")} PERIOD FOR SYSTEM_TIME on {table} {(enable ? "enables" : "disables")} the trigger that sets its period columns.");
        return [new PgSqlStatement(new PgEnableTrigger(pg, _names.Trigger(table, Schema.SchemaTranslator.PeriodTrigger(table.Name)), enable))];
    }

    /// <summary>
    /// <c>SET (SYSTEM_VERSIONING = OFF | ON (HISTORY_TABLE = h))</c>: the trigger that copies old
    /// rows into the history table is disabled or enabled.
    /// </summary>
    private List<PgPlStatement>? SetVersioning(AlterTableSetStatement set, BodyState st)
    {
        var (table, pg, model) = RealTable(set.SchemaObjectName, st);
        if (model?.Period?.HistoryTable is not { } history)
            return null;
        var option = set.Options.OfType<SystemVersioningTableOption>().Last();
        bool on = option.OptionState == OptionState.On;
        if (on && option.HistoryTable is { } named && !RealTable(named, st).Name.Equals(history))
            return null;
        st.C.Report(DiagnosticCodes.SystemVersioningInRoutine, Severity.Info,
            $"SYSTEM_VERSIONING = {(on ? "ON" : "OFF")} on {table} {(on ? "enables" : "disables")} the trigger that copies old rows into {history}"
            + (on && option.ConsistencyCheckEnabled == OptionState.On ? "; the data consistency check is not run" : "") + ".");
        return [new PgSqlStatement(new PgEnableTrigger(pg, _names.Trigger(table, Schema.SchemaTranslator.HistoryTrigger(table.Name)), on))];
    }

    // ---- routines ----

    /// <summary>DROP PROCEDURE / DROP FUNCTION → DROP ROUTINE, which drops either (a procedure may have become a function).</summary>
    /// <summary>
    /// DROP TRIGGER: a PostgreSQL trigger belongs to its table, and the converter made a trigger
    /// function for it (one per event: <c>name_insert</c>, … when there are several). Both are
    /// dropped, the table and events taken from the trigger's definition: the database's own, or
    /// one that a procedure creates with dynamic SQL.
    /// </summary>
    private List<PgPlStatement> DropTriggers(DropTriggerStatement drop, BodyState st)
    {
        var result = new List<PgPlStatement>();
        foreach (var o in drop.Objects)
        {
            var name = new ObjectName(o.SchemaIdentifier?.Value ?? st.C.Schema ?? SemanticModel.DefaultSchema, o.BaseIdentifier.Value);
            if (_model.ModuleTree(name) is not TriggerStatementBody t
                || (_model.Module(name)?.ParentTable ?? _model.ResolveRelation(t.TriggerObject.Name, name.Schema)) is not { } table)
                throw ExpressionTranslator.Untranslatable(drop,
                    $"DROP TRIGGER of {name}, which is neither a trigger of the source database nor one its procedures create with dynamic SQL of known text,",
                    DiagnosticCodes.DdlInRoutine);
            var events = t.TriggerActions.Select(a => a.TriggerActionType switch
            {
                TriggerActionType.Insert => "insert",
                TriggerActionType.Update => "update",
                TriggerActionType.Delete => "delete",
                _ => null,
            }).OfType<string>().Distinct().ToList();
            var pgTable = _names.Relation(table);
            foreach (string e in events)
            {
                string suffix = events.Count == 1 ? "" : "_" + e;
                result.Add(new PgSqlStatement(new PgDropTrigger(_names.Trigger(table, name.Name + suffix), pgTable, IfExists: drop.IsIfExists)));
                result.Add(new PgSqlStatement(new PgDropRoutine(_names.Routine(new ObjectName(name.Schema, name.Name + suffix)), IfExists: drop.IsIfExists)));
            }
        }
        st.C.Report(DiagnosticCodes.SchemaChangeInRoutine, Severity.Info,
            "DROP TRIGGER drops the PostgreSQL trigger on its table and the trigger function the converter made for it.");
        return result;
    }

    // ---- security policies ----

    private static readonly string[] PolicyCommands = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    private string PolicyName(ObjectName policy, string command) => _names.Alias(policy.Name + "_" + command.ToLowerInvariant());

    private static ObjectName PolicyObject(SchemaObjectName written, BodyState st) =>
        new(written.SchemaIdentifier?.Value ?? st.C.Schema ?? SemanticModel.DefaultSchema, written.BaseIdentifier.Value);

    /// <summary>
    /// CREATE SECURITY POLICY as row-level security. Each table gets ENABLE and FORCE ROW LEVEL
    /// SECURITY (SQL Server applies a policy to the table's owner too) and one permissive policy
    /// per command, since PostgreSQL ORs permissive policies: a FILTER predicate limits the rows
    /// SELECT, UPDATE and DELETE see (USING); a BLOCK predicate checks the rows INSERT and UPDATE
    /// write (AFTER INSERT/UPDATE, WITH CHECK) or the rows UPDATE and DELETE change (BEFORE
    /// UPDATE/DELETE, USING). A predicate is its inline function: <c>EXISTS (SELECT 1 FROM fn(col))</c>.
    /// </summary>
    private List<PgPlStatement> CreateSecurityPolicy(CreateSecurityPolicyStatement create, BodyState st)
    {
        var policy = PolicyObject(create.Name, st);
        bool enabled = !create.SecurityPolicyOptions.Any(o => o.OptionKind == SecurityPolicyOptionKind.State && o.OptionState == OptionState.Off);
        var result = new List<PgPlStatement>();
        foreach (var byTable in create.SecurityPredicateActions.GroupBy(a => PolicyObject(a.TargetObjectName, st), ObjectNameComparer.Instance))
        {
            var (table, pg, _) = RealTable(byTable.First().TargetObjectName, st);
            var uses = PolicyCommands.ToDictionary(c => c, _ => new List<PgExpr>());
            var checks = PolicyCommands.ToDictionary(c => c, _ => new List<PgExpr>());
            foreach (var a in byTable)
            {
                if (a.ActionType != SecurityPredicateActionType.Create)
                    throw ExpressionTranslator.Untranslatable(a, "ALTER or DROP of a predicate inside CREATE SECURITY POLICY", DiagnosticCodes.SecurityPolicy);
                var predicate = Predicate(a.FunctionCall, table, st);
                if (a.SecurityPredicateType == SecurityPredicateType.Filter)
                {
                    uses["SELECT"].Add(predicate);
                    uses["UPDATE"].Add(predicate);
                    uses["DELETE"].Add(predicate);
                    continue;
                }
                var operation = a.SecurityPredicateOperation;
                if (operation is SecurityPredicateOperation.All or SecurityPredicateOperation.AfterInsert)
                    checks["INSERT"].Add(predicate);
                if (operation is SecurityPredicateOperation.All or SecurityPredicateOperation.AfterUpdate)
                    checks["UPDATE"].Add(predicate);
                if (operation is SecurityPredicateOperation.All or SecurityPredicateOperation.BeforeUpdate)
                    uses["UPDATE"].Add(predicate);
                if (operation is SecurityPredicateOperation.All or SecurityPredicateOperation.BeforeDelete)
                    uses["DELETE"].Add(predicate);
            }
            foreach (string command in PolicyCommands)
            {
                PgExpr? @using = command == "INSERT" ? null : All(uses[command]);
                PgExpr? check = command is "SELECT" or "DELETE" ? null : All(checks[command]);
                result.Add(new PgSqlStatement(new PgCreatePolicy(PolicyName(policy, command), pg, command, @using, check)));
            }
            if (enabled)
            {
                result.Add(new PgSqlStatement(new PgRowSecurity(pg, "ENABLE")));
                result.Add(new PgSqlStatement(new PgRowSecurity(pg, "FORCE")));
            }
        }
        st.C.Report(DiagnosticCodes.SecurityPolicy, Severity.Info,
            $"CREATE SECURITY POLICY {policy} becomes row-level security: one policy per command on each table, and FORCE ROW LEVEL SECURITY so the table's owner is filtered too. A superuser, or a role with BYPASSRLS, still sees every row.");
        return result;
    }

    /// <summary>The conditions AND-ed (true when there are none).</summary>
    private static PgExpr All(List<PgExpr> conditions) =>
        conditions.Count == 0 ? PgLiteral.True : conditions.Skip(1).Aggregate(conditions[0], (a, b) => new PgBinary("AND", a, b));

    /// <summary>A predicate function call on the table's columns: <c>EXISTS (SELECT 1 FROM fn(col))</c>.</summary>
    private PgExpr Predicate(FunctionCall call, ObjectName table, BodyState st)
    {
        string? schema = call.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: [.., var s] } ? s.Value : null;
        var function = new ObjectName(schema ?? table.Schema, call.FunctionName.Value);
        var c = InTable(table, st);
        var arguments = call.Parameters.Select(p => _x.Scalar(p, c)).ToList();
        return new PgExists(new PgSelect
        {
            Targets = [new PgTarget(new PgLiteral("1"))],
            From = [new PgFunctionRef(_names.Routine(function).Qualified, arguments, null, [])],
        });
    }

    /// <summary>
    /// DROP SECURITY POLICY: the policies made for it dropped from each of its tables, and
    /// row-level security switched off there. Its tables come from the CREATE SECURITY POLICY a
    /// procedure of the database runs.
    /// </summary>
    private List<PgPlStatement> DropSecurityPolicies(DropSecurityPolicyStatement drop, BodyState st)
    {
        var result = new List<PgPlStatement>();
        foreach (var o in drop.Objects)
        {
            var policy = PolicyObject(o, st);
            if (_model.SecurityPolicy(policy) is not { } create)
                throw ExpressionTranslator.Untranslatable(drop,
                    $"DROP SECURITY POLICY of {policy}, which no procedure of the source database creates with dynamic SQL of known text,", DiagnosticCodes.SecurityPolicy);
            foreach (var target in create.SecurityPredicateActions.Select(a => a.TargetObjectName)
                         .DistinctBy(t => PolicyObject(t, st), ObjectNameComparer.Instance))
            {
                var (_, pg, _) = RealTable(target, st);
                foreach (string command in PolicyCommands)
                    result.Add(new PgSqlStatement(new PgDropPolicy(PolicyName(policy, command), pg, drop.IsIfExists)));
                result.Add(new PgSqlStatement(new PgRowSecurity(pg, "NO FORCE")));
                result.Add(new PgSqlStatement(new PgRowSecurity(pg, "DISABLE")));
            }
        }
        st.C.Report(DiagnosticCodes.SecurityPolicy, Severity.Info,
            "DROP SECURITY POLICY drops the row-level security policies made for it and switches row-level security off on its tables.");
        return result;
    }

    /// <summary>ALTER SEQUENCE … RESTART [WITH n]: the same in PostgreSQL.</summary>
    private List<PgPlStatement> RestartSequence(AlterSequenceStatement a, BodyState st)
    {
        var name = _model.ResolveRelation(a.Name, st.C.Schema) ?? new ObjectName(a.Name.SchemaIdentifier?.Value ?? st.C.Schema ?? SemanticModel.DefaultSchema, a.Name.BaseIdentifier.Value);
        var value = a.SequenceOptions[0] is ScalarExpressionSequenceOption { OptionValue: { } v } ? _x.Scalar(v, st.C) : null;
        return [new PgSqlStatement(new PgRestartSequence(_names.Relation(name), value))];
    }

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
