using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Expressions;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>
/// Translates a database's schema objects — schemas, types, sequences, tables, keys, constraints
/// and indexes — to PostgreSQL statements, one <see cref="TranslatedObject"/> per object and phase.
/// Tables are created with their columns only; keys, checks, foreign keys and indexes come in later
/// phases so data loads before them. Views and functions are converted in the Code phase;
/// procedures and triggers are listed as skipped until later Stage 3 work converts them.
/// </summary>
public sealed partial class SchemaTranslator
{
    /// <summary>The collation case-insensitive text columns get under <see cref="CaseInsensitiveStrategy.IcuCollation"/>.</summary>
    public static readonly PgName CaseInsensitiveCollation = new("public", "sql2pg_ci");

    private readonly ConversionOptions _options;
    private readonly NameMapper _names;
    private readonly TypeMapper _types;
    private SchemaExpressionTranslator _expressions = null!; // set by Translate, which builds the semantic model first

    /// <summary>Columns left out of each table's CREATE TABLE (untranslatable computed columns), by mapped name.</summary>
    private readonly Dictionary<ObjectName, HashSet<string>> _omitted = [];

    /// <summary>Columns other tables' foreign keys reference (<c>schema.table.column</c>, case-insensitive).</summary>
    private readonly HashSet<string> _referencedColumns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many generated constraint names of each kind a table has had, to number the next one as PostgreSQL does.</summary>
    private readonly Dictionary<string, int> _generatedNames = new(StringComparer.OrdinalIgnoreCase);

    public SchemaTranslator(ConversionOptions options)
    {
        _options = options;
        _names = new NameMapper(options);
        _types = new TypeMapper(options, _names);
    }

    public NameMapper Names => _names;

    /// <summary>The mssql_compat helpers the translated schema calls (known after <see cref="Translate"/>).</summary>
    public IReadOnlyCollection<string> UsedCompatFunctions =>
        [.. (_expressions?.UsedCompatFunctions ?? []).Union(_moduleCompat).Order(StringComparer.Ordinal)];

    private readonly SortedSet<string> _moduleCompat = new(StringComparer.Ordinal);

    public IReadOnlyList<TranslatedObject> Translate(SourceDatabase db)
    {
        var model = SemanticModel.Build(db);
        _expressions = new SchemaExpressionTranslator(_options, _names, _types, model);
        var queries = new QueryTranslator(new ExpressionTranslator(_options, _names, _types, model));
        var objects = new List<TranslatedObject>();
        var tables = db.Tables.Where(t => !Excluded(t.Name)).ToList();
        var sequences = db.Sequences.Where(s => !Excluded(s.Name)).ToList();
        var userTypes = db.UserTypes.Where(u => !Excluded(u.Name)).ToList();
        var modules = db.Modules.Where(m => !Excluded(m.Name)).ToList();

        foreach (var fk in tables.SelectMany(t => t.ForeignKeys))
            foreach (string c in fk.ReferencedColumns)
                _referencedColumns.Add($"{fk.Referenced}.{c}");

        // Names first, in a fixed order, so collisions resolve the same way every run.
        foreach (var t in tables)
        {
            _names.Relation(t.Name);
            foreach (var c in t.Columns)
                _names.Column(t.Name, c.Name);
        }
        foreach (var s in sequences)
            _names.Relation(s.Name);

        foreach (var u in userTypes)
            objects.Add(Guard(u.Name, ObjectKind.UserType, Phase.Types, () => UserType(u)));
        foreach (var s in sequences)
            objects.Add(Guard(s.Name, ObjectKind.Sequence, Phase.Sequences, () => Sequence(s)));
        foreach (var t in tables)
        {
            objects.Add(Guard(t.Name, ObjectKind.Table, Phase.Tables, () => Table(t)));
            objects.AddRange(GuardMany(t.Name, () => Constraints(t)));
            objects.AddRange(GuardMany(t.Name, () => Indexes(t)));
            objects.AddRange(GuardMany(t.Name, () => ForeignKeys(t)));
            objects.AddRange(GuardMany(t.Name, () => Versioning(t, model)));
        }
        var procedural = new ProceduralTranslator(queries);
        var triggerCounts = TriggerCounts(modules, model);
        foreach (var m in modules)
        {
            if (m.Kind is ObjectKind.View or ObjectKind.Function or ObjectKind.Procedure && string.IsNullOrWhiteSpace(m.Definition))
                objects.Add(Untranslated(m, (m.Kind == ObjectKind.View ? _names.Relation(m.Name) : _names.Routine(m.Name)).Qualified, [], DiagnosticCodes.EncryptedModule,
                    $"{m.Name} was created WITH ENCRYPTION, so SQL Server does not reveal its text; supply the original script."));
            else if (m.Kind == ObjectKind.View)
                objects.Add(Guard(m.Name, ObjectKind.View, Phase.Code, () => View(m, model, queries)));
            else if (m.Kind == ObjectKind.Function)
                objects.Add(Guard(m.Name, ObjectKind.Function, Phase.Code, () => Function(m, model, procedural)));
            else if (m.Kind == ObjectKind.Procedure)
                objects.Add(Guard(m.Name, ObjectKind.Procedure, Phase.Code, () => Procedure(m, model, procedural)));
            else if (m.Kind == ObjectKind.Trigger && string.IsNullOrWhiteSpace(m.Definition))
                objects.Add(Untranslated(m, _names.Routine(m.Name).Qualified, [], DiagnosticCodes.EncryptedModule,
                    $"{m.Name} was created WITH ENCRYPTION, so SQL Server does not reveal its text; supply the original script."));
            else if (m.Kind == ObjectKind.Trigger)
                objects.Add(Guard(m.Name, ObjectKind.Trigger, Phase.Triggers, () => Trigger(m, model, procedural, triggerCounts)));
            else
                objects.Add(new TranslatedObject(m.Name, m.Kind, m.Name.ToString(),
                    m.Kind == ObjectKind.Trigger ? Phase.Triggers : Phase.Code, [],
                    [new Diagnostic(DiagnosticCodes.NotYetSupported, Severity.Info, m.Name,
                        $"{m.Kind} {m.Name} is not converted.")], [])
                { Skipped = true });
        }
        objects = PropagateUnconverted(objects, model);
        foreach (var o in objects)
            _moduleCompat.UnionWith(o.CompatFunctions);

        if (_options.CaseInsensitive == CaseInsensitiveStrategy.IcuCollation
            && objects.Any(o => o.Statements.OfType<PgCreateTable>().Any(t => t.Columns.Any(c => c.Collation is not null))))
            objects.Add(new TranslatedObject(new ObjectName("public", CaseInsensitiveCollation.Name), ObjectKind.Collation,
                CaseInsensitiveCollation.Qualified, Phase.Types,
                [new PgCreateCollation(CaseInsensitiveCollation, "icu", "und-u-ks-level2", Deterministic: false)], [], []));

        // Schemas (other than public) of everything converted, and the extensions it needs.
        var schemaNames = objects.Where(o => !o.Skipped && o.Kind is not (ObjectKind.Extension or ObjectKind.Collation))
            .Select(o => _names.Schema(o.Source.Schema)).Distinct(StringComparer.Ordinal).Where(s => s != "public").Order(StringComparer.Ordinal);
        var schemaObjects = schemaNames.Select(s => new TranslatedObject(new ObjectName(s, s), ObjectKind.Schema,
            NameMapper.Quote(s), Phase.Schemas, [new PgCreateSchema(s)], [], [])).ToList();
        var extensionObjects = objects.SelectMany(o => o.Extensions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(e => new TranslatedObject(new ObjectName("", e), ObjectKind.Extension, NameMapper.Quote(e), Phase.Schemas,
                [new PgCreateExtension(e)], [], [])).ToList();

        return [.. extensionObjects, .. schemaObjects, .. objects];
    }

    private bool Excluded(ObjectName name) =>
        _options.ExcludeObjects.Any(pattern => Glob(pattern).IsMatch(name.ToString()));

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A translation that threw becomes a Manual object with an internal-error diagnostic, never a crash.</summary>
    private static TranslatedObject Guard(ObjectName name, ObjectKind kind, Phase phase, Func<TranslatedObject> translate)
    {
        try
        {
            return translate();
        }
        catch (Exception ex)
        {
            return new TranslatedObject(name, kind, name.ToString(), phase, [],
                [new Diagnostic(DiagnosticCodes.InternalError, Severity.Error, name, $"{ex.GetType().Name}: {ex.Message}")], []);
        }
    }

    private static IEnumerable<TranslatedObject> GuardMany(ObjectName table, Func<IEnumerable<TranslatedObject>> translate)
    {
        try
        {
            return translate().ToList();
        }
        catch (Exception ex)
        {
            return [new TranslatedObject(table, ObjectKind.Constraint, table.ToString(), Phase.Constraints, [],
                [new Diagnostic(DiagnosticCodes.InternalError, Severity.Error, table, $"{ex.GetType().Name}: {ex.Message}")], [])];
        }
    }

    // ---- types and sequences ----

    private TranslatedObject UserType(UserType u)
    {
        var name = _names.Type(u.Name);
        var diagnostics = new List<Diagnostic>();
        var extensions = new List<string>();
        if (!u.IsTableType)
        {
            var mapped = _types.Map(u.BaseType, u.Name);
            diagnostics.AddRange(mapped.Diagnostics);
            if (mapped.Extension is not null)
                extensions.Add(mapped.Extension);
            return new TranslatedObject(u.Name, ObjectKind.UserType, name.Qualified, Phase.Types,
                [new PgCreateDomain(name, mapped.PgType, !u.IsNullable)], diagnostics, []) { Extensions = extensions };
        }

        var fields = new List<PgField>();
        foreach (var c in u.TableColumns)
        {
            var mapped = _types.Map(c.Type, u.Name);
            diagnostics.AddRange(mapped.Diagnostics);
            if (mapped.Extension is not null)
                extensions.Add(mapped.Extension);
            fields.Add(new PgField(_names.Column(u.Name, c.Name), mapped.PgType));
        }
        diagnostics.Add(new Diagnostic(DiagnosticCodes.TableTypeAsComposite, Severity.Info, u.Name,
            $"Table type {u.Name} becomes a composite type; a table-valued parameter of it is an array of that type."));
        return new TranslatedObject(u.Name, ObjectKind.UserType, name.Qualified, Phase.Types,
            [new PgCreateCompositeType(name, fields)], diagnostics, []) { Extensions = extensions };
    }

    private TranslatedObject Sequence(Sequence s)
    {
        var name = _names.Relation(s.Name);
        var diagnostics = new List<Diagnostic>();
        string type = s.Type.Name switch
        {
            "tinyint" or "smallint" => "smallint",
            "int" => "integer",
            "bigint" => "bigint",
            _ => "bigint",
        };
        if (s.Type.Name is not ("tinyint" or "smallint" or "int" or "bigint"))
            diagnostics.Add(new Diagnostic(DiagnosticCodes.SequenceTypeChanged, Severity.Info, s.Name,
                $"Sequence {s.Name} is {s.Type}; PostgreSQL sequences are smallint, integer or bigint, so it becomes bigint."));
        return new TranslatedObject(s.Name, ObjectKind.Sequence, name.Qualified, Phase.Sequences,
            [new PgCreateSequence(name, type, s.Start, s.Increment, s.Min, s.Max, s.Cycle)], diagnostics, []);
    }

    // ---- tables ----


    private TranslatedObject Table(Table t)
    {
        var name = _names.Relation(t.Name);
        var diagnostics = new List<Diagnostic>();
        var extensions = new List<string>();
        var comments = new List<PgUntranslated>();
        var columns = new List<PgColumnDef>();
        var dependsOn = new List<ObjectName>();

        var omitted = _omitted[t.Name] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in t.Columns)
        {
            var column = Column(t, c, diagnostics, extensions, comments);
            if (column is not null)
                columns.Add(column);
            else
                omitted.Add(_names.Column(t.Name, c.Name));
            if (c.Type.UserTypeName is { } alias)
            {
                int dot = alias.IndexOf('.');
                dependsOn.Add(new ObjectName(alias[..dot], alias[(dot + 1)..]));
            }
        }

        return new TranslatedObject(t.Name, ObjectKind.Table, name.Qualified, Phase.Tables,
            [new PgCreateTable(name, columns) { Comments = comments }], diagnostics, dependsOn) { Extensions = extensions };
    }

    private PgColumnDef? Column(Table t, Column c, List<Diagnostic> diagnostics, List<string> extensions, List<PgUntranslated> comments)
    {
        string column = _names.Column(t.Name, c.Name);

        TypeMapping mapped;
        bool overridden = _options.TypeOverrides.Keys.Any(k => string.Equals(k, $"{t.Name}.{c.Name}", StringComparison.OrdinalIgnoreCase));
        if (c.Type.Name == "computed" && !overridden)
        {
            // Read from a script: SQL Server would derive the type from the expression, so derive it
            // here from the column types it uses, as far as they make it evident.
            string? inferred = c.Computed is null ? null : _expressions.InferType(c.Computed.Expression, t.Name, t.Columns);
            if (inferred is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.ComputedColumnTypeUnknown, Severity.Error, t.Name,
                    $"The type of computed column {t.Name}.{c.Name} cannot be worked out from its expression; add a typeOverrides entry for \"{t.Name}.{c.Name}\"."));
                comments.Add(new PgUntranslated(DiagnosticCodes.ComputedColumnTypeUnknown, $"{c.Name} AS {c.Computed?.Expression}"));
                return null;
            }
            diagnostics.Add(new Diagnostic(DiagnosticCodes.ComputedColumnTypeInferred, Severity.Info, t.Name,
                $"Computed column {t.Name}.{c.Name} was read from a script without its type; {inferred} is inferred from its expression."));
            mapped = new TypeMapping(inferred, []);
        }
        else
            mapped = _types.Map(c.Type, t.Name, c.Name);
        diagnostics.AddRange(mapped.Diagnostics);
        if (mapped.Extension is not null)
            extensions.Add(mapped.Extension);
        string type = mapped.PgType;

        PgIdentity? identity = null;
        if (c.Identity is { } id)
        {
            if (type.StartsWith("numeric", StringComparison.Ordinal))
            {
                type = (c.Type.Precision ?? 18) <= 9 ? "integer" : "bigint";
                diagnostics.Add(new Diagnostic(DiagnosticCodes.IdentityTypeChanged, Severity.Info, t.Name,
                    $"Identity column {t.Name}.{c.Name} is {c.Type}; a PostgreSQL identity must be an integer type, so it becomes {type}."));
            }
            identity = new PgIdentity(id.Seed, id.Increment);
        }

        PgGenerated? generated = null;
        if (c.Computed is { } computed)
        {
            var result = _expressions.Translate(computed.Expression, t.Name, t.Columns, immutable: true);
            diagnostics.AddRange(result.Diagnostics);
            extensions.AddRange(result.Extensions);
            if (result.Expression is null)
            {
                comments.Add(new PgUntranslated(DiagnosticCodes.UnsupportedExpression, $"{c.Name} AS {computed.Expression}"));
                return null;
            }
            if (IsVolatile(result.Expression))
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.GeneratedNotImmutable, Severity.Error, t.Name,
                    $"Computed column {t.Name}.{c.Name} uses a value that changes ({computed.Expression}); a PostgreSQL generated column must be immutable."));
                comments.Add(new PgUntranslated(DiagnosticCodes.GeneratedNotImmutable, $"{c.Name} AS {computed.Expression}"));
                return null;
            }
            // PostgreSQL 18 cannot index a VIRTUAL generated column (nor key one, or reference it
            // from a foreign key), so a column SQL Server indexes stays STORED.
            bool keyed = IsKeyedOrIndexed(t, c.Name);
            if (!computed.IsPersisted && _options.TargetVersion < 18)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.GeneratedColumnStored, Severity.Info, t.Name,
                    $"Computed column {t.Name}.{c.Name} is not persisted in SQL Server; before PostgreSQL 18 a generated column is always STORED."));
            else if (!computed.IsPersisted && keyed)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.GeneratedColumnStored, Severity.Info, t.Name,
                    $"Computed column {t.Name}.{c.Name} is not persisted in SQL Server, but it is indexed or part of a key, which PostgreSQL allows only for a STORED generated column."));
            // PostgreSQL 18 allows no user-defined function, an mssql_compat helper included, in a VIRTUAL generated column.
            bool usesHelper = result.CompatFunctions.Count > 0;
            if (!computed.IsPersisted && !keyed && usesHelper && _options.TargetVersion >= 18)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.GeneratedColumnStored, Severity.Info, t.Name,
                    $"Computed column {t.Name}.{c.Name} is not persisted in SQL Server, but it calls an mssql_compat helper ({string.Join(", ", result.CompatFunctions)}), which PostgreSQL allows only in a STORED generated column."));
            generated = new PgGenerated(result.Expression, computed.IsPersisted || keyed || usesHelper);
        }

        PgExpr? defaultValue = null;
        if (c.DefaultExpression is { } def && identity is null && generated is null)
        {
            var result = _expressions.Translate(def, t.Name, t.Columns, c.Type);
            diagnostics.AddRange(result.Diagnostics);
            extensions.AddRange(result.Extensions);
            if (result.Expression is null)
                comments.Add(new PgUntranslated(DiagnosticCodes.UnsupportedExpression, $"{c.Name} DEFAULT {def}"));
            defaultValue = result.Expression;
        }

        string? collation = null;
        if (_options.CaseInsensitive == CaseInsensitiveStrategy.IcuCollation && c.Type.Collation is { } coll
            && coll.Contains("_CI", StringComparison.OrdinalIgnoreCase) && (type == "text" || type.StartsWith("varchar", StringComparison.Ordinal) || type.StartsWith("char(", StringComparison.Ordinal)))
        {
            collation = CaseInsensitiveCollation.Qualified;
            if (_options.TargetVersion < 18)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.LikeOnNondeterministic, Severity.Warning, t.Name,
                    $"{t.Name}.{c.Name} gets a case-insensitive collation; PostgreSQL {_options.TargetVersion} cannot use LIKE on it (18 can)."));
        }

        if (c.IsRowGuidCol)
            diagnostics.Add(new Diagnostic(DiagnosticCodes.RowGuidColIgnored, Severity.Info, t.Name, $"ROWGUIDCOL on {t.Name}.{c.Name} is dropped."));
        if (c.IsSparse)
            diagnostics.Add(new Diagnostic(DiagnosticCodes.SparseIgnored, Severity.Info, t.Name, $"SPARSE on {t.Name}.{c.Name} is dropped."));

        return new PgColumnDef(column, type, NotNull: !c.IsNullable && generated is null, Default: defaultValue,
            Identity: identity, Generated: generated, Collation: collation);
    }

    /// <summary>Whether an index, the primary key, a unique constraint or a foreign key (either side) uses the column.</summary>
    private bool IsKeyedOrIndexed(Table t, string column)
    {
        bool Is(string name) => string.Equals(name, column, StringComparison.OrdinalIgnoreCase);
        return t.Indexes.Any(i => i.Keys.Any(k => Is(k.Name)) || i.Included.Any(Is))
               || t.PrimaryKey?.Columns.Any(k => Is(k.Name)) == true
               || t.Uniques.Any(u => u.Columns.Any(k => Is(k.Name)))
               || t.ForeignKeys.Any(f => f.Columns.Any(Is))
               || _referencedColumns.Contains($"{t.Name}.{column}");
    }

    /// <summary>
    /// Calls PostgreSQL does not mark immutable, which no generated column may use: values that
    /// change from call to call, and functions that depend on settings (concat and to_char format
    /// dates by DateStyle; the TRY_CAST and ISDATE helpers parse dates).
    /// </summary>
    private bool IsVolatile(PgExpr e) => e switch
    {
        PgKeyword { Sql: "localtimestamp" or "current_user" } => true,
        PgFunctionCall { Name: "now" or "gen_random_uuid" or "uuidv7" or "random" or "nextval" or "concat" or "concat_ws"
            or "to_char" or "to_date" or "to_timestamp" or "current_setting" or "current_database" } => true,
        PgFunctionCall f when f.Name.StartsWith(NameMapper.Quote(_options.CompatSchemaName) + ".try_cast_", StringComparison.Ordinal)
                              || f.Name == NameMapper.Quote(_options.CompatSchemaName) + ".isdate" => true,
        PgFunctionCall f => f.Arguments.Any(IsVolatile),
        PgUnary u => IsVolatile(u.Operand),
        PgBinary b => IsVolatile(b.Left) || IsVolatile(b.Right),
        PgCast c => IsVolatile(c.Operand),
        PgCase c => (c.Operand is not null && IsVolatile(c.Operand)) || c.Whens.Any(w => IsVolatile(w.Condition) || IsVolatile(w.Result)) || (c.Else is not null && IsVolatile(c.Else)),
        PgAtTimeZone a => IsVolatile(a.Operand) || IsVolatile(a.Zone),
        _ => false,
    };

    /// <summary>
    /// A name for a constraint SQL Server named itself, as PostgreSQL would name it: the base name
    /// the first time, then with 1, 2, … appended (<c>jobs_check</c>, <c>jobs_check1</c>).
    /// </summary>
    private string GeneratedName(ObjectName table, string baseName)
    {
        string key = $"{table}:{baseName}";
        int n = _generatedNames.GetValueOrDefault(key);
        _generatedNames[key] = n + 1;
        return n == 0 ? baseName : $"{baseName}{n}";
    }

    /// <summary>The columns of <paramref name="mapped"/> that the table was created without.</summary>
    private IReadOnlyList<string> Missing(ObjectName table, IEnumerable<string> mapped) =>
        _omitted.TryGetValue(table, out var omitted) ? mapped.Where(omitted.Contains).Distinct().ToList() : [];

    private static IEnumerable<string> ColumnsIn(PgExpr? e) => e switch
    {
        null => [],
        PgColumnRef c => [c.Name],
        PgUnary u => ColumnsIn(u.Operand),
        PgBinary b => ColumnsIn(b.Left).Concat(ColumnsIn(b.Right)),
        PgIsNull n => ColumnsIn(n.Operand),
        PgInList i => ColumnsIn(i.Operand).Concat(i.Values.SelectMany(ColumnsIn)),
        PgBetween b => ColumnsIn(b.Operand).Concat(ColumnsIn(b.Low)).Concat(ColumnsIn(b.High)),
        PgLike l => ColumnsIn(l.Operand).Concat(ColumnsIn(l.Pattern)),
        PgCase c => ColumnsIn(c.Operand).Concat(c.Whens.SelectMany(w => ColumnsIn(w.Condition).Concat(ColumnsIn(w.Result)))).Concat(ColumnsIn(c.Else)),
        PgCast c => ColumnsIn(c.Operand),
        PgFunctionCall f => f.Arguments.SelectMany(ColumnsIn),
        PgAtTimeZone a => ColumnsIn(a.Operand),
        _ => [],
    };

    /// <summary>A stand-in for a key, index or constraint on a column the table does not have: the original kept as a comment.</summary>
    private static TranslatedObject OnMissingColumns(ObjectName source, ObjectKind kind, string target, Phase phase, ObjectName table,
        IReadOnlyList<string> missing, string original) =>
        new(source, kind, target, phase, [new PgUntranslated(DiagnosticCodes.UsesUntranslatedColumn, original)],
            [new Diagnostic(DiagnosticCodes.UsesUntranslatedColumn, Severity.Error, table,
                $"{original} uses {string.Join(", ", missing)}, which {table} was created without (its definition could not be translated).")],
            [table]);

    // ---- keys and checks ----

    private IEnumerable<TranslatedObject> Constraints(Table t)
    {
        var table = _names.Relation(t.Name);
        string Col(string c) => _names.Column(t.Name, c);

        if (t.PrimaryKey is { } pk)
        {
            string name = _names.Index(t.Name, pk.Name ?? GeneratedName(t.Name, $"{t.Name.Name}_pkey"));
            var pkColumns = pk.Columns.Select(c => Col(c.Name)).ToList();
            if (Missing(t.Name, pkColumns) is { Count: > 0 } missingPk)
                yield return OnMissingColumns(new ObjectName(t.Name.Schema, name), ObjectKind.Constraint, $"{table.Qualified}.{NameMapper.Quote(name)}",
                    Phase.Constraints, t.Name, missingPk, $"PRIMARY KEY {name} ({string.Join(", ", pk.Columns.Select(c => c.Name))})");
            else
            {
                var diagnostics = new List<Diagnostic>();
                if (pk.IsClustered)
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.ClusteredIgnored, Severity.Info, t.Name,
                        $"{t.Name} is clustered in SQL Server; PostgreSQL does not keep rows in index order (CLUSTER can reorder them once)."));
                yield return new TranslatedObject(new ObjectName(t.Name.Schema, pk.Name ?? $"{t.Name.Name}_pkey"), ObjectKind.Constraint,
                    $"{table.Qualified}.{NameMapper.Quote(name)}", Phase.Constraints,
                    [new PgAddPrimaryKey(table, name, pkColumns)], diagnostics, [t.Name]);
            }
        }

        foreach (var u in t.Uniques)
        {
            string source = u.Name ?? GeneratedName(t.Name, $"{t.Name.Name}_{string.Join("_", u.Columns.Select(c => c.Name))}_key");
            string name = _names.Index(t.Name, source);
            if (Missing(t.Name, u.Columns.Select(c => Col(c.Name))) is { Count: > 0 } missingUnique)
            {
                yield return OnMissingColumns(new ObjectName(t.Name.Schema, source), ObjectKind.Constraint, $"{table.Qualified}.{NameMapper.Quote(name)}",
                    Phase.Constraints, t.Name, missingUnique, $"UNIQUE {source} ({string.Join(", ", u.Columns.Select(c => c.Name))})");
                continue;
            }
            yield return new TranslatedObject(new ObjectName(t.Name.Schema, source), ObjectKind.Constraint,
                $"{table.Qualified}.{NameMapper.Quote(name)}", Phase.Constraints,
                [new PgAddUnique(table, name, u.Columns.Select(c => Col(c.Name)).ToList())], [], [t.Name]);
        }

        foreach (var ck in t.Checks)
        {
            string source = ck.Name ?? GeneratedName(t.Name, $"{t.Name.Name}_check");
            string name = _names.Constraint(t.Name, source);
            var result = _expressions.Translate(ck.Expression, t.Name, t.Columns, isPredicate: true);
            if (Missing(t.Name, ColumnsIn(result.Expression)) is { Count: > 0 } missingCheck)
            {
                yield return OnMissingColumns(new ObjectName(t.Name.Schema, source), ObjectKind.Constraint, $"{table.Qualified}.{NameMapper.Quote(name)}",
                    Phase.Constraints, t.Name, missingCheck, $"CHECK {source} ({ck.Expression.Trim()})");
                continue;
            }
            var diagnostics = result.Diagnostics.ToList();
            diagnostics.AddRange(NotValidNotes(t.Name, $"check {source}", ck.IsDisabled, ck.IsNotTrusted));
            PgStatement statement = result.Expression is null
                ? new PgUntranslated(DiagnosticCodes.UnsupportedExpression, $"ALTER TABLE {t.Name} ADD CONSTRAINT {source} CHECK ({ck.Expression.Trim()})")
                : new PgAddCheck(table, name, result.Expression, ck.IsDisabled || ck.IsNotTrusted);
            yield return new TranslatedObject(new ObjectName(t.Name.Schema, source), ObjectKind.Constraint,
                $"{table.Qualified}.{NameMapper.Quote(name)}", Phase.Constraints, [statement], diagnostics, [t.Name])
            { Extensions = result.Extensions };
        }
    }

    /// <summary>
    /// SQL Server skips checking existing rows for an untrusted constraint, and does not enforce a
    /// disabled one at all. NOT VALID matches the first; for the second PostgreSQL still checks new
    /// rows, which SQL Server would not.
    /// </summary>
    private static IEnumerable<Diagnostic> NotValidNotes(ObjectName table, string what, bool disabled, bool notTrusted)
    {
        if (disabled)
            yield return new Diagnostic(DiagnosticCodes.DisabledConstraintEnforced, Severity.Warning, table,
                $"The {what} on {table} is disabled in SQL Server; it is created NOT VALID, but PostgreSQL enforces it for new and updated rows.");
        else if (notTrusted)
            yield return new Diagnostic(DiagnosticCodes.ConstraintNotValid, Severity.Info, table,
                $"The {what} on {table} is not trusted by SQL Server (created WITH NOCHECK); it is created NOT VALID, so existing rows are not checked.");
    }

    private IEnumerable<TranslatedObject> ForeignKeys(Table t)
    {
        var table = _names.Relation(t.Name);
        foreach (var fk in t.ForeignKeys)
        {
            string source = fk.Name ?? GeneratedName(t.Name, $"{t.Name.Name}_{string.Join("_", fk.Columns)}_fkey");
            string name = _names.Constraint(t.Name, source);
            var referenced = _names.Relation(fk.Referenced);
            var missingFk = Missing(t.Name, fk.Columns.Select(c => _names.Column(t.Name, c)))
                .Concat(Missing(fk.Referenced, fk.ReferencedColumns.Select(c => _names.Column(fk.Referenced, c)))).ToList();
            if (missingFk.Count > 0)
            {
                yield return OnMissingColumns(new ObjectName(t.Name.Schema, source), ObjectKind.Constraint, $"{table.Qualified}.{NameMapper.Quote(name)}",
                    Phase.ForeignKeys, t.Name, missingFk, $"FOREIGN KEY {source} ({string.Join(", ", fk.Columns)}) REFERENCES {fk.Referenced}");
                continue;
            }
            yield return new TranslatedObject(new ObjectName(t.Name.Schema, source), ObjectKind.Constraint,
                $"{table.Qualified}.{NameMapper.Quote(name)}", Phase.ForeignKeys,
                [new PgAddForeignKey(table, name, fk.Columns.Select(c => _names.Column(t.Name, c)).ToList(), referenced,
                    fk.ReferencedColumns.Select(c => _names.Column(fk.Referenced, c)).ToList(),
                    Action(fk.OnDelete), Action(fk.OnUpdate), fk.IsDisabled || fk.IsNotTrusted)],
                NotValidNotes(t.Name, $"foreign key {source}", fk.IsDisabled, fk.IsNotTrusted).ToList(),
                [t.Name, fk.Referenced]);
        }
    }

    private static string? Action(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => null,
    };

    // ---- indexes ----

    private IEnumerable<TranslatedObject> Indexes(Table t)
    {
        var table = _names.Relation(t.Name);
        foreach (var ix in t.Indexes)
        {
            var source = new ObjectName(t.Name.Schema, ix.Name);
            string? method = ix.Type switch
            {
                "rowstore" => null,
                "spatial" when _options.AllowsExtension(PermittedExtensions.PostGis) => "gist",
                _ => "unsupported",
            };
            if (method == "unsupported")
            {
                yield return new TranslatedObject(source, ObjectKind.Index, $"{table.Schema}.{ix.Name}", Phase.Indexes, [],
                    [new Diagnostic(DiagnosticCodes.IndexTypeUnsupported, Severity.Warning, t.Name,
                        $"{ix.Type} index {ix.Name} on {t.Name} has no PostgreSQL equivalent and is not created.")], [t.Name])
                { Skipped = true };
                continue;
            }

            string name = _names.Index(t.Name, ix.Name);
            var indexColumns = ix.Keys.Select(k => _names.Column(t.Name, k.Name)).Concat(ix.Included.Select(c => _names.Column(t.Name, c)));
            if (Missing(t.Name, indexColumns) is { Count: > 0 } missingIndex)
            {
                yield return OnMissingColumns(source, ObjectKind.Index, $"{NameMapper.Quote(table.Schema)}.{NameMapper.Quote(name)}", Phase.Indexes,
                    t.Name, missingIndex, $"INDEX {ix.Name} ({string.Join(", ", ix.Keys.Select(k => k.Name))})");
                continue;
            }
            var diagnostics = new List<Diagnostic>();
            var extensions = new List<string>();
            if (method == "gist")
                extensions.Add(PermittedExtensions.PostGis);
            if (ix.IsClustered)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.ClusteredIgnored, Severity.Info, t.Name,
                    $"{ix.Name} is the clustered index of {t.Name}; PostgreSQL does not keep rows in index order (CLUSTER can reorder them once)."));
            PgExpr? where = null;
            if (ix.FilterExpression is { } filter)
            {
                var result = _expressions.Translate(filter, t.Name, t.Columns, isPredicate: true);
                diagnostics.AddRange(result.Diagnostics);
                where = result.Expression;
                if (where is null)
                {
                    yield return new TranslatedObject(source, ObjectKind.Index, $"{table.Schema}.{name}", Phase.Indexes,
                        [new PgUntranslated(DiagnosticCodes.UnsupportedExpression, $"CREATE INDEX {ix.Name} ON {t.Name} … WHERE {filter}")],
                        diagnostics, [t.Name]);
                    continue;
                }
            }
            yield return new TranslatedObject(source, ObjectKind.Index, $"{NameMapper.Quote(table.Schema)}.{NameMapper.Quote(name)}", Phase.Indexes,
                [new PgCreateIndex(name, table, ix.Keys.Select(k => new PgIndexKey(_names.Column(t.Name, k.Name), k.Descending)).ToList(),
                    ix.Included.Select(c => _names.Column(t.Name, c)).ToList(), ix.IsUnique, where, method)],
                diagnostics, [t.Name]) { Extensions = extensions };
        }
    }
}
