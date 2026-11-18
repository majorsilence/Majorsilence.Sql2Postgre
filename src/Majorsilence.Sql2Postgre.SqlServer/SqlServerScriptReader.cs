using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Shared;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.SqlServer;

/// <summary>
/// Reads <c>CREATE</c> scripts (a folder of <c>.sql</c> files, for users without a live database)
/// into the same model the catalog reader produces. Each <c>GO</c> batch is parsed on its own
/// with ScriptDom, so one batch that does not parse is reported (<c>S2P0001</c>) and the rest are
/// still read.
/// </summary>
/// <remarks>
/// Where SQL Server fills something in when a statement leaves it out, the reader fills it in
/// the same way, so a script and the database it creates read the same: an unqualified name is
/// in <c>dbo</c>; <c>varchar</c> without a length is <c>varchar(1)</c>; <c>decimal</c> is
/// <c>decimal(18,0)</c>; <c>datetime2</c>, <c>time</c> and <c>datetimeoffset</c> have scale 7;
/// <c>float(1)</c>–<c>float(24)</c> is <c>real</c>; <c>rowversion</c> is <c>timestamp</c>; primary
/// key, identity and <c>timestamp</c> columns are NOT NULL and other columns are nullable; a primary
/// key is clustered unless it says otherwise. Three things only the catalog knows are left
/// empty: the database's default collation (so a column's collation is set only where the
/// script names one), row counts, and dependencies (WP 2.1 derives those from the parsed code).
/// Expressions (defaults, checks, computed columns, filters) are kept as the script writes them;
/// the catalog keeps SQL Server's normalized form, and the translator parses either.
/// </remarks>
public static partial class SqlServerScriptReader
{
    public const string DefaultSchema = "dbo";

    public static SourceReadResult Read(IEnumerable<(string Path, string Text)> files, string databaseName = "script")
    {
        var state = new ReaderState();
        foreach (var (path, text) in files)
        {
            foreach (var (batch, firstLine) in SplitBatches(text))
            {
                var parser = new TSql160Parser(initialQuotedIdentifiers: true);
                var fragment = parser.Parse(new StringReader(batch), out var errors);
                if (errors.Count > 0)
                {
                    var e = errors[0];
                    state.Diagnostics.Add(new Diagnostic(DiagnosticCodes.ParseError, Severity.Error, null,
                        $"{path}({e.Line + firstLine - 1},{e.Column}): {e.Message}", new SourceSpan(e.Line + firstLine - 1, e.Column, 0)));
                    continue;
                }
                foreach (var statement in ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements))
                    state.Add(statement);
            }
        }
        return state.Build(databaseName);
    }

    public static SourceReadResult ReadDirectory(string directory, string? databaseName = null) =>
        Read(Directory.EnumerateFiles(directory, "*.sql", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(p => (p, File.ReadAllText(p))),
            databaseName ?? Path.GetFileName(Path.GetFullPath(directory)));

    /// <summary>Batches with the 1-based line each starts on, split on <c>GO</c> lines.</summary>
    internal static IEnumerable<(string Batch, int FirstLine)> SplitBatches(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();
        int start = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (GoLine().IsMatch(lines[i]))
            {
                if (!string.IsNullOrWhiteSpace(current.ToString()))
                    yield return (current.ToString(), start);
                current.Clear();
                start = i + 2;
                continue;
            }
            current.Append(lines[i]).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(current.ToString()))
            yield return (current.ToString(), start);
    }

    [GeneratedRegex(@"^[ \t]*GO[ \t]*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    internal static string TextOf(TSqlFragment fragment)
    {
        var sb = new StringBuilder();
        for (int i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            sb.Append(fragment.ScriptTokenStream[i].Text);
        return sb.ToString();
    }

    private static ObjectName NameOf(SchemaObjectName name) =>
        new(name.SchemaIdentifier?.Value ?? DefaultSchema, name.BaseIdentifier.Value);

    private sealed class TableBuilder(ObjectName name)
    {
        public ObjectName Name { get; } = name;
        public List<Column> Columns { get; } = [];
        public PrimaryKey? PrimaryKey { get; set; }
        public List<UniqueConstraint> Uniques { get; } = [];
        public List<ForeignKey> ForeignKeys { get; } = [];
        public List<CheckConstraint> Checks { get; } = [];
        public List<TableIndex> Indexes { get; } = [];
        public SystemTimePeriod? Period { get; set; }
    }

    private sealed class ReaderState
    {
        public List<Diagnostic> Diagnostics { get; } = [];
        private readonly SortedSet<string> _schemas = new(StringComparer.Ordinal) { DefaultSchema };
        private readonly Dictionary<ObjectName, TableBuilder> _tables = [];
        private readonly List<Sequence> _sequences = [];
        private readonly Dictionary<ObjectName, UserType> _userTypes = [];
        private readonly List<Module> _modules = [];

        public void Add(TSqlStatement statement)
        {
            switch (statement)
            {
                case CreateSchemaStatement s:
                    _schemas.Add(s.Name.Value);
                    foreach (var inner in s.StatementList?.Statements ?? [])
                        Add(inner);
                    break;
                case CreateTypeUddtStatement u:
                    AddAlias(u);
                    break;
                case CreateTypeTableStatement t:
                    var tableTypeName = Track(NameOf(t.Name));
                    var columns = t.Definition.ColumnDefinitions.Select((c, i) => ReadColumn(c, i + 1, inPrimaryKey: false)).ToList();
                    _userTypes[tableTypeName] = new UserType(tableTypeName, new SqlServerType("table"), false, true, columns);
                    break;
                case CreateSequenceStatement seq:
                    AddSequence(seq);
                    break;
                case CreateTableStatement ct:
                    AddTable(ct);
                    break;
                case AlterTableAddTableElementStatement add:
                    AlterAdd(add);
                    break;
                case AlterTableConstraintModificationStatement mod:
                    AlterEnforcement(mod);
                    break;
                case AlterTableSetStatement set:
                    AlterSet(set);
                    break;
                case CreateIndexStatement ix:
                    AddIndex(ix);
                    break;
                case CreateColumnStoreIndexStatement cs:
                    AddColumnstore(cs);
                    break;
                case CreateSpatialIndexStatement sp:
                    AddSpecialIndex(sp.Object, sp.Name.Value, [sp.SpatialColumnName.Value], "spatial");
                    break;
                case CreateXmlIndexStatement xml:
                    AddSpecialIndex(xml.OnName, xml.Name.Value, [xml.XmlColumn.Value], "xml");
                    break;
                case CreateFullTextIndexStatement ft:
                    AddFullTextIndex(ft);
                    break;
                case CreateViewStatement or CreateOrAlterViewStatement:
                    AddModule(((ViewStatementBody)statement).SchemaObjectName, ObjectKind.View, statement);
                    break;
                case CreateFunctionStatement or CreateOrAlterFunctionStatement:
                    AddModule(((FunctionStatementBody)statement).Name, ObjectKind.Function, statement);
                    break;
                case CreateProcedureStatement or CreateOrAlterProcedureStatement:
                    AddModule(((ProcedureStatementBody)statement).ProcedureReference.Name, ObjectKind.Procedure, statement);
                    break;
                case CreateTriggerStatement or CreateOrAlterTriggerStatement:
                    var trigger = (TriggerStatementBody)statement;
                    if (trigger.TriggerObject.TriggerScope == TriggerScope.Normal)
                        AddModule(trigger.Name, ObjectKind.Trigger, statement, NameOf(trigger.TriggerObject.Name));
                    break;
            }
        }

        private ObjectName Track(ObjectName name)
        {
            _schemas.Add(name.Schema);
            return name;
        }

        private void AddAlias(CreateTypeUddtStatement u)
        {
            var name = Track(NameOf(u.Name));
            var baseType = ScriptDomTypes.TypeOf(u.DataType);
            _userTypes[name] = new UserType(name, baseType, u.NullableConstraint?.Nullable ?? true, false, []);
        }

        private void AddSequence(CreateSequenceStatement seq)
        {
            var name = Track(NameOf(seq.Name));
            var type = new SqlServerType("bigint");
            long start = 0, increment = 1;
            long? min = null, max = null;
            bool cycle = false, startSet = false;
            foreach (var option in seq.SequenceOptions)
            {
                switch (option)
                {
                    case DataTypeSequenceOption dt:
                        type = ScriptDomTypes.TypeOf(dt.DataType);
                        break;
                    case ScalarExpressionSequenceOption sv when !sv.NoValue:
                        long value = Integer(sv.OptionValue);
                        switch (sv.OptionKind)
                        {
                            case SequenceOptionKind.Start: start = value; startSet = true; break;
                            case SequenceOptionKind.Increment: increment = value; break;
                            case SequenceOptionKind.MinValue: min = value; break;
                            case SequenceOptionKind.MaxValue: max = value; break;
                        }
                        break;
                    case { OptionKind: SequenceOptionKind.Cycle }:
                        cycle = !option.NoValue;
                        break;
                }
            }
            // SQL Server starts an ascending sequence at its minimum and a descending one at its
            // maximum when START WITH is left out; the type's own bounds stand in for a missing one.
            min ??= TypeMin(type.Name);
            max ??= TypeMax(type.Name);
            if (!startSet)
                start = increment > 0 ? min.Value : max.Value;
            _sequences.Add(new Sequence(name, type, start, increment, min, max, cycle));
        }

        private void AddTable(CreateTableStatement ct)
        {
            var table = new TableBuilder(Track(NameOf(ct.SchemaObjectName)));
            var definition = ct.Definition;
            var pkColumns = PrimaryKeyColumns(definition);
            int ordinal = 0;
            foreach (var column in definition.ColumnDefinitions)
            {
                string columnName = column.ColumnIdentifier.Value;
                table.Columns.Add(ReadColumn(column, ++ordinal, pkColumns.Contains(columnName)));
                foreach (var constraint in column.Constraints)
                    AddConstraint(table, constraint, [columnName], trusted: true);
            }
            foreach (var constraint in definition.TableConstraints)
                AddConstraint(table, constraint, [], trusted: true);
            foreach (var index in definition.Indexes)
                table.Indexes.Add(new TableIndex(index.Name.Value, index.Columns.Select(IndexColumnOf).ToList(),
                    index.IncludeColumns.Select(ColumnName).ToList(), index.Unique,
                    index.IndexType?.IndexTypeKind is IndexTypeKind.Clustered or IndexTypeKind.ClusteredColumnStore,
                    index.FilterPredicate is null ? null : TextOf(index.FilterPredicate)));
            if (definition.SystemTimePeriod is { } period)
            {
                var versioning = ct.Options.OfType<SystemVersioningTableOption>().FirstOrDefault(o => o.OptionState == OptionState.On);
                table.Period = new SystemTimePeriod(period.StartTimeColumn.Value, period.EndTimeColumn.Value,
                    versioning?.HistoryTable is { } history ? NameOf(history) : null);
                if (versioning is not null && versioning.HistoryTable is null)
                    Diagnostics.Add(new Diagnostic(DiagnosticCodes.SystemVersioning, Severity.Warning, table.Name,
                        $"{table.Name} is system-versioned without HISTORY_TABLE: SQL Server names the history table itself, so a script does not have it. Its history is not kept."));
            }
            _tables[table.Name] = table;
        }

        /// <summary>Columns of a primary key declared on the table or on a column; SQL Server makes them NOT NULL.</summary>
        private static HashSet<string> PrimaryKeyColumns(TableDefinition definition)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in definition.TableConstraints.OfType<UniqueConstraintDefinition>().Where(u => u.IsPrimaryKey))
                names.UnionWith(c.Columns.Select(x => ColumnName(x.Column)));
            foreach (var column in definition.ColumnDefinitions)
                if (column.Constraints.OfType<UniqueConstraintDefinition>().Any(u => u.IsPrimaryKey))
                    names.Add(column.ColumnIdentifier.Value);
            return names;
        }

        private Column ReadColumn(ColumnDefinition c, int ordinal, bool inPrimaryKey)
        {
            var type = c.DataType is null ? new SqlServerType("sql_variant") : ScriptDomTypes.TypeOf(c.DataType);
            if (c.Collation is not null)
                type = type with { Collation = c.Collation.Value };
            var identity = c.IdentityOptions is null ? null : new IdentitySpec(
                c.IdentityOptions.IdentitySeed is null ? 1 : Integer(c.IdentityOptions.IdentitySeed),
                c.IdentityOptions.IdentityIncrement is null ? 1 : Integer(c.IdentityOptions.IdentityIncrement));
            bool? declaredNullable = c.Constraints.OfType<NullableConstraintDefinition>().LastOrDefault()?.Nullable;
            bool nullable = declaredNullable ?? !(inPrimaryKey || identity is not null || type.Name == "timestamp");
            var computed = c.ComputedColumnExpression is null ? null : new ComputedSpec(TextOf(c.ComputedColumnExpression), c.IsPersisted);
            return new Column(c.ColumnIdentifier.Value, ordinal, computed is null ? type : new SqlServerType("computed"),
                computed is null ? nullable : true, identity,
                c.DefaultConstraint is null ? null : TextOf(c.DefaultConstraint.Expression),
                c.DefaultConstraint?.ConstraintIdentifier?.Value, computed, c.IsRowGuidCol,
                c.StorageOptions?.SparseOption == SparseColumnOption.Sparse);
        }

        private void AddConstraint(TableBuilder table, ConstraintDefinition constraint, IReadOnlyList<string> columnScope, bool trusted)
        {
            string? name = constraint.ConstraintIdentifier?.Value;
            switch (constraint)
            {
                case UniqueConstraintDefinition u:
                    var keys = u.Columns.Count > 0 ? u.Columns.Select(IndexColumnOf).ToList() : columnScope.Select(n => new IndexColumn(n)).ToList();
                    if (u.IsPrimaryKey)
                        table.PrimaryKey = new PrimaryKey(name, keys, u.Clustered ?? !table.Indexes.Any(i => i.IsClustered));
                    else
                        table.Uniques.Add(new UniqueConstraint(name, keys));
                    break;
                case ForeignKeyConstraintDefinition fk:
                    var columns = fk.Columns.Count > 0 ? fk.Columns.Select(i => i.Value).ToList() : columnScope.ToList();
                    table.ForeignKeys.Add(new ForeignKey(name, columns, NameOf(fk.ReferenceTableName),
                        fk.ReferencedTableColumns.Select(i => i.Value).ToList(), Action(fk.DeleteAction), Action(fk.UpdateAction),
                        IsDisabled: false, IsNotTrusted: !trusted));
                    break;
                case CheckConstraintDefinition ck:
                    table.Checks.Add(new CheckConstraint(name, TextOf(ck.CheckCondition), IsDisabled: false, IsNotTrusted: !trusted));
                    break;
                case DefaultConstraintDefinition df when df.Column is not null:
                    int i = table.Columns.FindIndex(c => string.Equals(c.Name, df.Column.Value, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                        table.Columns[i] = table.Columns[i] with { DefaultExpression = TextOf(df.Expression), DefaultConstraintName = name };
                    break;
            }
        }

        private void AlterAdd(AlterTableAddTableElementStatement add)
        {
            if (!_tables.TryGetValue(NameOf(add.SchemaObjectName), out var table))
                return;
            bool trusted = add.ExistingRowsCheckEnforcement != ConstraintEnforcement.NoCheck;
            foreach (var column in add.Definition.ColumnDefinitions)
                table.Columns.Add(ReadColumn(column, table.Columns.Count + 1, inPrimaryKey: false));
            foreach (var constraint in add.Definition.TableConstraints)
                AddConstraint(table, constraint, [], trusted);
            if (add.Definition.SystemTimePeriod is { } period)
                table.Period = new SystemTimePeriod(period.StartTimeColumn.Value, period.EndTimeColumn.Value, table.Period?.HistoryTable);
        }

        /// <summary><c>ALTER TABLE … SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = h) | OFF)</c> on a table with a period.</summary>
        private void AlterSet(AlterTableSetStatement set)
        {
            if (!_tables.TryGetValue(NameOf(set.SchemaObjectName), out var table) || table.Period is null)
                return;
            foreach (var option in set.Options.OfType<SystemVersioningTableOption>())
                table.Period = table.Period with { HistoryTable = option.OptionState == OptionState.On && option.HistoryTable is { } h ? NameOf(h) : null };
        }

        /// <summary><c>ALTER TABLE … NOCHECK CONSTRAINT</c> disables a foreign key or check, and SQL Server then no longer trusts it.</summary>
        private void AlterEnforcement(AlterTableConstraintModificationStatement mod)
        {
            if (!_tables.TryGetValue(NameOf(mod.SchemaObjectName), out var table))
                return;
            bool disable = mod.ConstraintEnforcement == ConstraintEnforcement.NoCheck;
            bool Named(string? n) => mod.All || mod.ConstraintNames.Any(c => string.Equals(c.Value, n, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < table.Checks.Count; i++)
                if (Named(table.Checks[i].Name))
                    table.Checks[i] = table.Checks[i] with { IsDisabled = disable, IsNotTrusted = disable || table.Checks[i].IsNotTrusted };
            for (int i = 0; i < table.ForeignKeys.Count; i++)
                if (Named(table.ForeignKeys[i].Name))
                    table.ForeignKeys[i] = table.ForeignKeys[i] with { IsDisabled = disable, IsNotTrusted = disable || table.ForeignKeys[i].IsNotTrusted };
        }

        private void AddIndex(CreateIndexStatement ix)
        {
            // A clustered index on a view makes it an indexed view.
            if (ix.Clustered == true && _modules.FindIndex(m => m.Kind == ObjectKind.View && m.Name == NameOf(ix.OnName)) is var view and >= 0)
            {
                _modules[view] = _modules[view] with { IsIndexed = true };
                return;
            }
            if (!_tables.TryGetValue(NameOf(ix.OnName), out var table))
                return;
            table.Indexes.Add(new TableIndex(ix.Name.Value, ix.Columns.Select(IndexColumnOf).ToList(),
                ix.IncludeColumns.Select(ColumnName).ToList(), ix.Unique, ix.Clustered == true,
                ix.FilterPredicate is null ? null : TextOf(ix.FilterPredicate)));
        }

        /// <summary>A clustered columnstore index covers every column, in table order, as the catalog lists it.</summary>
        private void AddColumnstore(CreateColumnStoreIndexStatement cs)
        {
            if (!_tables.TryGetValue(NameOf(cs.OnName), out var table))
                return;
            bool clustered = cs.Clustered == true;
            var columns = clustered
                ? table.Columns.Select(c => new IndexColumn(c.Name)).ToList()
                : cs.Columns.Select(c => new IndexColumn(ColumnName(c))).ToList();
            table.Indexes.Add(new TableIndex(cs.Name.Value, columns, [], false, clustered,
                cs.FilterPredicate is null ? null : TextOf(cs.FilterPredicate), "columnstore"));
        }

        /// <summary>A spatial, XML or full-text index, named as the catalog reader names it (full-text indexes have no name of their own).</summary>
        private void AddSpecialIndex(SchemaObjectName on, string name, IReadOnlyList<string> columns, string type)
        {
            if (_tables.TryGetValue(NameOf(on), out var table))
                table.Indexes.Add(new TableIndex(name, columns.Select(c => new IndexColumn(c)).ToList(), [], false, false, null, type));
        }

        /// <summary>
        /// A full-text index, named as the catalog reader names it. Its key is the KEY INDEX's single
        /// column (the primary key's when the name is not one the script gave).
        /// </summary>
        private void AddFullTextIndex(CreateFullTextIndexStatement ft)
        {
            if (!_tables.TryGetValue(NameOf(ft.OnName), out var table))
                return;
            string keyIndex = ft.KeyIndexName.Value;
            bool Named(string? name) => string.Equals(name, keyIndex, StringComparison.OrdinalIgnoreCase);
            var keys = Named(table.PrimaryKey?.Name) ? table.PrimaryKey!.Columns
                : table.Uniques.FirstOrDefault(u => Named(u.Name))?.Columns
                ?? table.Indexes.FirstOrDefault(i => Named(i.Name))?.Keys
                ?? table.PrimaryKey?.Columns;
            var columns = new List<FullTextColumn>();
            foreach (var c in ft.FullTextIndexColumns)
            {
                int? language = null;
                if (c.LanguageTerm is { } term)
                {
                    string written = term.Identifier?.Value ?? TextOf(term.ValueExpression);
                    language = FullTextLanguages.Parse(written);
                    if (language is null)
                        Diagnostics.Add(new Diagnostic(DiagnosticCodes.FullTextSettingUnmapped, Severity.Warning, table.Name,
                            $"The full-text language {written} of {table.Name}.{c.Name.Value} is not one SQL Server lists; the column is indexed as English."));
                }
                columns.Add(new FullTextColumn(c.Name.Value, language, c.TypeColumn?.Value));
            }
            var stopList = ft.Options.OfType<StopListFullTextIndexOption>().FirstOrDefault() switch
            {
                null => FullTextIndex.SystemStopList,
                { IsOff: true } => FullTextIndex.NoStopList,
                { StopListName: { } name } => name.Value,
                _ => FullTextIndex.SystemStopList,
            };
            table.Indexes.Add(new TableIndex($"fulltext_{table.Name.Name}", columns.Select(c => new IndexColumn(c.Name)).ToList(), [], false, false, null,
                "fulltext", new FullTextIndex(keys is [var key] ? key.Name : null, columns, stopList)));
        }

        private void AddModule(SchemaObjectName name, ObjectKind kind, TSqlStatement statement, ObjectName? parent = null) =>
            _modules.Add(new Module(Track(NameOf(name)), kind, TextOf(statement), parent));

        public SourceReadResult Build(string databaseName)
        {
            // Columns typed with an alias type take its base type, recorded with the alias name.
            var tables = _tables.Values.Select(t => new Table(t.Name,
                    t.Columns.Select(ResolveAlias).ToList(), t.PrimaryKey, t.Uniques.ToList(),
                    t.ForeignKeys.ToList(), t.Checks.ToList(),
                    t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal).ToList(), Period: t.Period))
                .OrderBy(t => t.Name.Schema, StringComparer.Ordinal).ThenBy(t => t.Name.Name, StringComparer.Ordinal)
                .ToList();
            var userTypes = _userTypes.Values.Select(u => u with { TableColumns = u.TableColumns.Select(ResolveAlias).ToList() })
                .OrderBy(u => u.Name.Schema, StringComparer.Ordinal).ThenBy(u => u.Name.Name, StringComparer.Ordinal).ToList();
            var db = new SourceDatabase(databaseName, "", 160, _schemas.ToList(), tables,
                _sequences.OrderBy(s => s.Name.Schema, StringComparer.Ordinal).ThenBy(s => s.Name.Name, StringComparer.Ordinal).ToList(),
                userTypes,
                _modules.OrderBy(m => m.Name.Schema, StringComparer.Ordinal).ThenBy(m => m.Name.Name, StringComparer.Ordinal).ToList(),
                []);
            return new SourceReadResult(db, Diagnostics);
        }

        private Column ResolveAlias(Column column)
        {
            if (column.Type.Name != "alias")
                return column;
            var alias = column.Type.UserTypeName!;
            int dot = alias.IndexOf('.');
            var name = new ObjectName(alias[..dot], alias[(dot + 1)..]);
            if (!_userTypes.TryGetValue(name, out var userType) || userType.IsTableType)
                return column with { Type = new SqlServerType(name.Name.ToLowerInvariant()) };
            return column with { Type = userType.BaseType with { UserTypeName = alias, Collation = column.Type.Collation } };
        }
    }

    private static IndexColumn IndexColumnOf(ColumnWithSortOrder c) =>
        new(ColumnName(c.Column), c.SortOrder == SortOrder.Descending);

    private static string ColumnName(ColumnReferenceExpression c) => c.MultiPartIdentifier.Identifiers[^1].Value;

    private static ReferentialAction Action(DeleteUpdateAction action) => action switch
    {
        DeleteUpdateAction.Cascade => ReferentialAction.Cascade,
        DeleteUpdateAction.SetNull => ReferentialAction.SetNull,
        DeleteUpdateAction.SetDefault => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };

    /// <summary>An integer literal, possibly signed or parenthesized, as identity seeds and sequence options write them.</summary>
    private static long Integer(ScalarExpression expression) => expression switch
    {
        IntegerLiteral i => long.Parse(i.Value, CultureInfo.InvariantCulture),
        NumericLiteral n => (long)decimal.Parse(n.Value, CultureInfo.InvariantCulture),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } u => -Integer(u.Expression),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } u => Integer(u.Expression),
        ParenthesisExpression p => Integer(p.Expression),
        _ => throw new FormatException($"Expected an integer literal, got '{TextOf(expression)}'."),
    };

    private static long TypeMin(string type) => type switch
    {
        "tinyint" => 0,
        "smallint" => short.MinValue,
        "int" => int.MinValue,
        _ => long.MinValue,
    };

    private static long TypeMax(string type) => type switch
    {
        "tinyint" => byte.MaxValue,
        "smallint" => short.MaxValue,
        "int" => int.MaxValue,
        _ => long.MaxValue,
    };
}
