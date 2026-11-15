using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Shared;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Semantics;

/// <summary>
/// What every name in the database is and what type every expression has, for the translators.
/// Built once from a <see cref="SourceDatabase"/>: tables, views (their columns inferred from
/// their SELECT), table-valued functions (their result columns), scalar function return types,
/// procedure parameters, user types and sequences. Module bodies are parsed with ScriptDom; the
/// parsed trees are kept, so the translators do not parse them again.
/// </summary>
/// <remarks>
/// An unqualified name inside a module is looked up in the module's own schema first and then in
/// <c>dbo</c>, which is how it resolves for the usual case of objects owned by <c>dbo</c> and
/// modules that live beside the objects they use.
/// </remarks>
public sealed partial class SemanticModel
{
    private readonly Dictionary<ObjectName, Table> _tables = new(ObjectNameComparer.Instance);
    private readonly Dictionary<ObjectName, RelationInfo> _relations = new(ObjectNameComparer.Instance);
    private readonly Dictionary<ObjectName, RoutineInfo> _routines = new(ObjectNameComparer.Instance);
    private readonly Dictionary<ObjectName, UserType> _userTypes = new(ObjectNameComparer.Instance);
    private readonly HashSet<ObjectName> _sequences = new(ObjectNameComparer.Instance);
    private readonly Dictionary<ObjectName, TSqlStatement> _moduleTrees = new(ObjectNameComparer.Instance);
    private readonly Dictionary<ObjectName, Module> _modules = new(ObjectNameComparer.Instance);
    private readonly HashSet<ObjectName> _resolvingViews = new(ObjectNameComparer.Instance);
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly List<ObjectDependency> _dependencies = [];
    private string _databaseName = "";

    public const string DefaultSchema = "dbo";

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>The catalog's dependencies plus those found by walking every module's parsed body.</summary>
    public IReadOnlyList<ObjectDependency> Dependencies => _dependencies;

    public static SemanticModel Build(SourceDatabase db)
    {
        var model = new SemanticModel();
        model.Load(db);
        return model;
    }

    private void Load(SourceDatabase db)
    {
        _databaseName = db.Name;
        foreach (var t in db.Tables)
        {
            _tables[t.Name] = t;
            _relations[t.Name] = new RelationInfo(t.Name, ObjectKind.Table, t.Columns.Select(c => new ColumnInfo(c.Name, ColumnType(c))).ToList());
        }
        foreach (var u in db.UserTypes)
            _userTypes[u.Name] = u;
        foreach (var s in db.Sequences)
            _sequences.Add(s.Name);

        foreach (var m in db.Modules)
        {
            _modules[m.Name] = m;
            if (string.IsNullOrWhiteSpace(m.Definition))
                continue;
            var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(m.Definition), out var errors);
            if (errors.Count > 0 || fragment is not TSqlScript script || script.Batches.SelectMany(b => b.Statements).FirstOrDefault() is not { } statement)
            {
                _diagnostics.Add(new Diagnostic(DiagnosticCodes.ParseError, Severity.Error, m.Name,
                    $"The definition of {m.Name} does not parse: {errors.FirstOrDefault()?.Message}"));
                continue;
            }
            _moduleTrees[m.Name] = statement;
        }

        // Routine signatures first: a view or function may use a table-valued function.
        foreach (var (name, statement) in _moduleTrees)
            if (statement is FunctionStatementBody or ProcedureStatementBody)
                _routines[name] = Signature(name, statement);

        foreach (var (name, statement) in _moduleTrees)
            if (statement is ViewStatementBody)
                View(name);

        var dependencies = new HashSet<ObjectDependency>(db.Dependencies);
        foreach (var (name, statement) in _moduleTrees)
            foreach (var to in References(name, statement))
                dependencies.Add(new ObjectDependency(name, to));
        _dependencies.AddRange(dependencies.OrderBy(d => d.From.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.To.ToString(), StringComparer.OrdinalIgnoreCase));
    }

    // ---- lookup ----

    public Table? Table(ObjectName name) => _tables.GetValueOrDefault(name);

    public RelationInfo? Relation(ObjectName name)
    {
        if (_relations.TryGetValue(name, out var r))
            return r;
        if (_moduleTrees.TryGetValue(name, out var s) && s is ViewStatementBody)
            return View(name);
        return null;
    }

    public RoutineInfo? Routine(ObjectName name) => _routines.GetValueOrDefault(name);

    public Module? Module(ObjectName name) => _modules.GetValueOrDefault(name);

    /// <summary>The parsed CREATE statement of a module, or null when it did not parse (or is encrypted).</summary>
    public TSqlStatement? ModuleTree(ObjectName name) => _moduleTrees.GetValueOrDefault(name);

    public bool IsSequence(ObjectName name) => _sequences.Contains(name);

    public UserType? UserType(ObjectName name) => _userTypes.GetValueOrDefault(name);

    /// <summary>
    /// The object a written name refers to: qualified names as written; an unqualified name in the
    /// context schema, then in <c>dbo</c>. Names with a database or server part are external and
    /// return null (see <see cref="IsExternal"/>).
    /// </summary>
    public ObjectName? Resolve(SchemaObjectName written, string? contextSchema, Func<ObjectName, bool> exists)
    {
        if (IsExternal(written, _databaseName))
            return null;
        string name = written.BaseIdentifier.Value;
        if (written.SchemaIdentifier is { } schema)
            return new ObjectName(schema.Value, name);
        foreach (string s in new[] { contextSchema, DefaultSchema }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = new ObjectName(s, name);
            if (exists(candidate))
                return candidate;
        }
        return new ObjectName(contextSchema ?? DefaultSchema, name);
    }

    public ObjectName? ResolveRelation(SchemaObjectName written, string? contextSchema) =>
        Resolve(written, contextSchema, n => Relation(n) is not null || Routine(n) is { IsTableValued: true });

    public ObjectName? ResolveRoutine(SchemaObjectName written, string? contextSchema) =>
        Resolve(written, contextSchema, n => _routines.ContainsKey(n));

    /// <summary>A four-part name (a linked server), or a three-part name of another database.</summary>
    public static bool IsExternal(SchemaObjectName written, string currentDatabase) =>
        written.ServerIdentifier is not null
        || (written.DatabaseIdentifier is { } db && !string.Equals(db.Value, currentDatabase, StringComparison.OrdinalIgnoreCase));

    public bool IsExternal(SchemaObjectName written) => IsExternal(written, _databaseName);

    // ---- types ----

    /// <summary>A column's type, as read (an alias type is already its base type, with the alias name kept).</summary>
    private static SqlServerType ColumnType(Column c) => c.Type;

    /// <summary>A declared type: built-in as written, or a user alias type resolved to its base type.</summary>
    public SqlServerType TypeOf(DataTypeReference type, string? contextSchema = null)
    {
        var t = ScriptDomTypes.TypeOf(type);
        if (t.Name != "alias" || t.UserTypeName is null)
            return t;
        int dot = t.UserTypeName.IndexOf('.');
        var name = new ObjectName(t.UserTypeName[..dot], t.UserTypeName[(dot + 1)..]);
        if (_userTypes.TryGetValue(name, out var u) && !u.IsTableType)
            return u.BaseType with { UserTypeName = t.UserTypeName };
        if (type is UserDataTypeReference { Name.SchemaIdentifier: null } && contextSchema is not null
            && _userTypes.TryGetValue(new ObjectName(contextSchema, name.Name), out var inContext) && !inContext.IsTableType)
            return inContext.BaseType with { UserTypeName = $"{contextSchema}.{name.Name}" };
        return t;
    }

    /// <summary>
    /// The SQL Server type of an expression, following SQL Server's data type precedence: literals,
    /// columns and variables through the scope, CAST/CONVERT, arithmetic and concatenation, CASE
    /// (highest precedence of its results), built-in functions, scalar UDFs, and scalar subqueries.
    /// Null when it is not evident — never a guess.
    /// </summary>
    public SqlServerType? TypeOf(ScalarExpression e, Scope scope, string? contextSchema = null) => e switch
    {
        ParenthesisExpression p => TypeOf(p.Expression, scope, contextSchema),
        IntegerLiteral i => long.TryParse(i.Value, out long v) && v is >= int.MinValue and <= int.MaxValue
            ? new SqlServerType("int") : new SqlServerType("decimal", Precision: i.Value.Length, Scale: 0),
        NumericLiteral n => NumericLiteralType(n.Value),
        RealLiteral => new SqlServerType("float", Precision: 53),
        MoneyLiteral => new SqlServerType("money"),
        StringLiteral { IsNational: true } s => new SqlServerType("nvarchar", Math.Max(1, s.Value.Length)),
        StringLiteral s => new SqlServerType("varchar", Math.Max(1, s.Value.Length)),
        BinaryLiteral b => new SqlServerType("varbinary", Math.Max(1, (b.Value.Length - 2) / 2)),
        NullLiteral => null,
        VariableReference v => scope.TryVariable(v.Name, out var vt) ? vt : null,
        GlobalVariableExpression g => TypeRules.BuiltinReturnType(g.Name.ToUpperInvariant(), []),
        ColumnReferenceExpression { ColumnType: Microsoft.SqlServer.TransactSql.ScriptDom.ColumnType.Regular, MultiPartIdentifier: { } id } =>
            scope.ResolveColumn(id.Identifiers.Select(x => x.Value).ToList())?.Column.Type,
        UnaryExpression u => TypeOf(u.Expression, scope, contextSchema),
        BinaryExpression b => Binary(b, scope, contextSchema),
        CastCall c => TypeOf(c.DataType, contextSchema),
        ConvertCall c => TypeOf(c.DataType, contextSchema),
        TryCastCall c => TypeOf(c.DataType, contextSchema),
        TryConvertCall c => TypeOf(c.DataType, contextSchema),
        SearchedCaseExpression c => Results(c.WhenClauses.Select(w => w.ThenExpression).Append(c.ElseExpression), scope, contextSchema),
        SimpleCaseExpression c => Results(c.WhenClauses.Select(w => w.ThenExpression).Append(c.ElseExpression), scope, contextSchema),
        IIfCall i => Results([i.ThenExpression, i.ElseExpression], scope, contextSchema),
        CoalesceExpression c => Results(c.Expressions, scope, contextSchema),
        NullIfExpression n => TypeOf(n.FirstExpression, scope, contextSchema),
        ParameterlessCall p => p.ParameterlessCallType == ParameterlessCallType.CurrentTimestamp
            ? new SqlServerType("datetime") : new SqlServerType("nvarchar", 128),
        FunctionCall f => Function(f, scope, contextSchema),
        LeftFunctionCall l => TypeRules.BuiltinReturnType("LEFT", l.Parameters.Select(x => TypeOf(x, scope, contextSchema)).ToList()),
        RightFunctionCall r => TypeRules.BuiltinReturnType("RIGHT", r.Parameters.Select(x => TypeOf(x, scope, contextSchema)).ToList()),
        ScalarSubquery s => OutputColumns(s.QueryExpression, scope, contextSchema).FirstOrDefault()?.Type,
        NextValueForExpression n => ResolveSequenceType(n.SequenceName, contextSchema),
        _ => null,
    };

    private static SqlServerType NumericLiteralType(string text)
    {
        string digits = text.TrimStart('-', '+');
        int point = digits.IndexOf('.');
        int scale = point < 0 ? 0 : digits.Length - point - 1;
        int precision = Math.Max(scale, digits.Replace(".", "").TrimStart('0').Length);
        return new SqlServerType("decimal", Precision: Math.Max(1, precision), Scale: scale);
    }

    private SqlServerType? Binary(BinaryExpression b, Scope scope, string? contextSchema)
    {
        var left = TypeOf(b.FirstExpression, scope, contextSchema);
        var right = TypeOf(b.SecondExpression, scope, contextSchema);
        if (b.BinaryExpressionType == BinaryExpressionType.Add && (TypeRules.IsString(left) || TypeRules.IsString(right)))
        {
            // A string plus a number converts the string to the number (precedence); two strings concatenate.
            if (TypeRules.IsNumeric(left) || TypeRules.IsNumeric(right))
                return TypeRules.IsNumeric(left) ? left : right;
            return TypeRules.Concatenation(left, right);
        }
        return TypeRules.Arithmetic(left, right);
    }

    private SqlServerType? Results(IEnumerable<ScalarExpression?> results, Scope scope, string? contextSchema)
    {
        SqlServerType? best = null;
        foreach (var r in results)
        {
            if (r is null or NullLiteral)
                continue;
            var t = TypeOf(r, scope, contextSchema);
            if (t is null)
                return null;
            best = best is null ? t : TypeRules.Combine(best, t);
        }
        return best;
    }

    private SqlServerType? Function(FunctionCall f, Scope scope, string? contextSchema)
    {
        if (f.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: 1 } parts })
        {
            var routine = Routine(new ObjectName(parts[0].Value, f.FunctionName.Value));
            return routine?.ReturnType;
        }
        if (f.CallTarget is not null)
            return null;
        string name = f.FunctionName.Value.ToUpperInvariant();
        var args = f.Parameters.Select(p => TypeOf(p, scope, contextSchema)).ToList();
        return TypeRules.BuiltinReturnType(name, args);
    }

    private SqlServerType? ResolveSequenceType(SchemaObjectName name, string? contextSchema) =>
        Resolve(name, contextSchema, IsSequence) is { } n && IsSequence(n) ? new SqlServerType("bigint") : null;

    // ---- query scopes and output columns ----

    /// <summary>
    /// The scope of a query specification: its FROM clause's relations (tables, views, TVFs,
    /// derived tables, CTEs, table variables, temp tables, APPLY sides) bound under their aliases.
    /// </summary>
    public Scope QueryScope(QuerySpecification query, Scope outer, string? contextSchema)
    {
        var scope = outer.Child();
        foreach (var reference in query.FromClause?.TableReferences ?? [])
            BindTableReference(reference, scope, contextSchema);
        return scope;
    }

    public void BindTableReference(TableReference reference, Scope scope, string? contextSchema)
    {
        switch (reference)
        {
            case QualifiedJoin j:
                BindTableReference(j.FirstTableReference, scope, contextSchema);
                BindTableReference(j.SecondTableReference, scope, contextSchema);
                break;
            case UnqualifiedJoin j:
                BindTableReference(j.FirstTableReference, scope, contextSchema);
                // The right side of APPLY sees the left side's columns.
                BindTableReference(j.SecondTableReference, scope, contextSchema);
                break;
            case JoinParenthesisTableReference p:
                BindTableReference(p.Join, scope, contextSchema);
                break;
            case NamedTableReference n:
                var relation = NamedRelation(n.SchemaObject, scope, contextSchema);
                scope.Bind(new Binding(n.Alias?.Value ?? n.SchemaObject.BaseIdentifier.Value,
                    relation ?? new RelationInfo(null, ObjectKind.Table, [])));
                break;
            case VariableTableReference v:
                scope.Bind(new Binding(v.Alias?.Value ?? v.Variable.Name, scope.Table(v.Variable.Name) ?? new RelationInfo(null, ObjectKind.Table, [])));
                break;
            case QueryDerivedTable d:
                var columns = OutputColumns(d.QueryExpression, scope.Parent ?? scope, contextSchema);
                if (d.Columns.Count > 0)
                    columns = columns.Select((c, i) => i < d.Columns.Count ? c with { Name = d.Columns[i].Value } : c).ToList();
                scope.Bind(new Binding(d.Alias?.Value ?? "", new RelationInfo(null, ObjectKind.Table, columns)));
                break;
            case InlineDerivedTable v:
                var first = v.RowValues.FirstOrDefault()?.ColumnValues ?? [];
                scope.Bind(new Binding(v.Alias?.Value ?? "", new RelationInfo(null, ObjectKind.Table,
                    v.Columns.Select((c, i) => new ColumnInfo(c.Value, i < first.Count ? TypeOf(first[i], scope, contextSchema) : null)).ToList())));
                break;
            case SchemaObjectFunctionTableReference f:
                var routine = ResolveRoutine(f.SchemaObject, contextSchema) is { } rn ? Routine(rn) : null;
                scope.Bind(new Binding(f.Alias?.Value ?? f.SchemaObject.BaseIdentifier.Value,
                    new RelationInfo(routine?.Name, ObjectKind.Function, routine?.ResultColumns ?? [])));
                break;
        }
    }

    /// <summary>A named relation in FROM: a CTE in scope, a temp table, a table, a view or a TVF.</summary>
    private RelationInfo? NamedRelation(SchemaObjectName written, Scope scope, string? contextSchema)
    {
        if (written.SchemaIdentifier is null && scope.Cte(written.BaseIdentifier.Value) is { } cte)
            return cte;
        if (written.BaseIdentifier.Value.StartsWith('#'))
            return scope.Table(written.BaseIdentifier.Value);
        if (ResolveRelation(written, contextSchema) is not { } name)
            return null;
        if (Relation(name) is { } relation)
            return relation;
        return Routine(name) is { ResultColumns: { } cols } routine ? new RelationInfo(routine.Name, ObjectKind.Function, cols) : null;
    }

    /// <summary>Registers a WITH clause's CTEs in the scope (a recursive CTE's columns come from its anchor member).</summary>
    public void DefineCtes(WithCtesAndXmlNamespaces? with, Scope scope, string? contextSchema)
    {
        foreach (var cte in with?.CommonTableExpressions ?? [])
        {
            var query = cte.QueryExpression;
            var anchor = query;
            while (anchor is BinaryQueryExpression bin)
                anchor = bin.FirstQueryExpression;
            var columns = OutputColumns(anchor, scope, contextSchema);
            if (cte.Columns.Count > 0)
                columns = cte.Columns.Select((c, i) => new ColumnInfo(c.Value, i < columns.Count ? columns[i].Type : null)).ToList();
            scope.DefineCte(cte.ExpressionName.Value, new RelationInfo(null, ObjectKind.Table, columns));
        }
    }

    /// <summary>The columns a query returns, named as SQL Server names them, with their types where evident.</summary>
    public IReadOnlyList<ColumnInfo> OutputColumns(QueryExpression query, Scope outer, string? contextSchema)
    {
        switch (query)
        {
            case QueryParenthesisExpression p:
                return OutputColumns(p.QueryExpression, outer, contextSchema);
            case BinaryQueryExpression b:
                return OutputColumns(b.FirstQueryExpression, outer, contextSchema);
            case QuerySpecification spec:
                var scope = QueryScope(spec, outer, contextSchema);
                var columns = new List<ColumnInfo>();
                foreach (var element in spec.SelectElements)
                {
                    switch (element)
                    {
                        case SelectScalarExpression s:
                            columns.Add(new ColumnInfo(OutputName(s), TypeOf(s.Expression, scope, contextSchema)));
                            break;
                        case SelectStarExpression star when star.Qualifier is { } q:
                            var parts = q.Identifiers.Select(i => i.Value).ToList();
                            if (scope.ResolveQualifier(parts[^1], parts.Count > 1 ? parts[^2] : null) is { } qualified)
                                columns.AddRange(qualified.Relation.Columns);
                            break;
                        case SelectStarExpression:
                            foreach (var binding in scope.Bindings)
                                columns.AddRange(binding.Relation.Columns);
                            break;
                    }
                }
                return columns;
            default:
                return [];
        }
    }

    /// <summary>A select item's name: its alias, else the column's own name, else empty (SQL Server's "no column name").</summary>
    public static string OutputName(SelectScalarExpression s) =>
        s.ColumnName?.Value
        ?? (s.Expression is ColumnReferenceExpression { MultiPartIdentifier: { } id } ? id.Identifiers[^1].Value : "");

    // ---- views and routines ----

    private RelationInfo? View(ObjectName name)
    {
        if (_relations.TryGetValue(name, out var known))
            return known;
        if (!_moduleTrees.TryGetValue(name, out var s) || s is not ViewStatementBody view || !_resolvingViews.Add(name))
            return null;
        try
        {
            var root = new Scope();
            DefineCtes(view.SelectStatement.WithCtesAndXmlNamespaces, root, name.Schema);
            var columns = OutputColumns(view.SelectStatement.QueryExpression, root, name.Schema).ToList();
            if (view.Columns.Count > 0)
                columns = view.Columns.Select((c, i) => new ColumnInfo(c.Value, i < columns.Count ? columns[i].Type : null)).ToList();
            var relation = new RelationInfo(name, ObjectKind.View, columns);
            _relations[name] = relation;
            return relation;
        }
        finally
        {
            _resolvingViews.Remove(name);
        }
    }

    private RoutineInfo Signature(ObjectName name, TSqlStatement statement)
    {
        IList<ProcedureParameter> parameters = statement switch
        {
            FunctionStatementBody f => f.Parameters,
            ProcedureStatementBody p => p.Parameters,
            _ => [],
        };
        var paramInfos = parameters.Select(p => new ParameterInfo(p.VariableName.Value.TrimStart('@'),
            TypeOf(p.DataType, name.Schema), p.Modifier == ParameterModifier.Output, p.Value is not null)).ToList();

        if (statement is not FunctionStatementBody function)
            return new RoutineInfo(name, ObjectKind.Procedure, paramInfos, null, null);

        var scope = new Scope();
        foreach (var p in paramInfos)
            scope.DefineVariable(p.Name, p.Type);
        return function.ReturnType switch
        {
            ScalarFunctionReturnType scalar => new RoutineInfo(name, ObjectKind.Function, paramInfos, TypeOf(scalar.DataType, name.Schema), null),
            SelectFunctionReturnType inline => new RoutineInfo(name, ObjectKind.Function, paramInfos, null,
                OutputColumns(inline.SelectStatement.QueryExpression, WithCtes(inline.SelectStatement, scope, name.Schema), name.Schema)),
            TableValuedFunctionReturnType table => new RoutineInfo(name, ObjectKind.Function, paramInfos, null,
                table.DeclareTableVariableBody.Definition.ColumnDefinitions
                    .Select(c => new ColumnInfo(c.ColumnIdentifier.Value, c.DataType is null ? null : TypeOf(c.DataType, name.Schema))).ToList()),
            _ => new RoutineInfo(name, ObjectKind.Function, paramInfos, null, null),
        };
    }

    private Scope WithCtes(SelectStatement select, Scope scope, string schema)
    {
        DefineCtes(select.WithCtesAndXmlNamespaces, scope, schema);
        return scope;
    }

    // ---- dependencies ----

    /// <summary>The objects a module's body names (tables, views, functions, procedures, sequences), resolved; external names reported.</summary>
    private IEnumerable<ObjectName> References(ObjectName module, TSqlStatement statement)
    {
        var collector = new ReferenceCollector();
        statement.Accept(collector);
        var found = new HashSet<ObjectName>(ObjectNameComparer.Instance);
        foreach (var written in collector.Names)
        {
            if (IsExternal(written))
            {
                _diagnostics.Add(new Diagnostic(DiagnosticCodes.ExternalReference, Severity.Error, module,
                    $"{module} refers to {string.Join('.', new[] { written.ServerIdentifier, written.DatabaseIdentifier, written.SchemaIdentifier, written.BaseIdentifier }.Where(i => i is not null).Select(i => i!.Value))}, in another database or on a linked server; PostgreSQL cannot reach it from here."));
                continue;
            }
            if (written.BaseIdentifier.Value.StartsWith('#') || written.BaseIdentifier.Value.StartsWith('@'))
                continue;
            var name = Resolve(written, module.Schema, n => _tables.ContainsKey(n) || _moduleTrees.ContainsKey(n) || _sequences.Contains(n) || _modules.ContainsKey(n));
            if (name is not null && !ObjectNameComparer.Instance.Equals(name, module)
                && (_tables.ContainsKey(name) || _modules.ContainsKey(name) || _sequences.Contains(name)))
                found.Add(name);
        }
        return found;
    }

    private sealed class ReferenceCollector : TSqlFragmentVisitor
    {
        public List<SchemaObjectName> Names { get; } = [];

        public override void Visit(NamedTableReference node) => Names.Add(node.SchemaObject);

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            if (!IsXmlNodesCall(node.SchemaObject))
                Names.Add(node.SchemaObject);
        }

        public override void Visit(NextValueForExpression node) => Names.Add(node.SequenceName);

        public override void Visit(ProcedureReference node) => Names.Add(node.Name);

        /// <summary>
        /// <c>schema.function(…)</c> names a user-defined function. A longer call target is a method
        /// on a column or variable (<c>p.Demographics.value(…)</c>, <c>t.geo.STDistance(…)</c>), not an object.
        /// </summary>
        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: 1 } parts })
            {
                var name = new SchemaObjectName();
                foreach (var p in parts)
                    name.Identifiers.Add(p);
                name.Identifiers.Add(node.FunctionName);
                Names.Add(name);
            }
        }
    }
}

public sealed partial class SemanticModel
{
    /// <summary>
    /// <c>CROSS APPLY t.doc.nodes('…')</c>: the xml <c>nodes()</c> method on a column, which
    /// ScriptDom reads as a three-part function name in FROM.
    /// </summary>
    public static bool IsXmlNodesCall(SchemaObjectName name) =>
        string.Equals(name.BaseIdentifier.Value, "nodes", StringComparison.OrdinalIgnoreCase)
        && name.SchemaIdentifier is not null && name.DatabaseIdentifier is not null;
}

/// <summary>Compares object names as SQL Server does under its usual collations: case-insensitively.</summary>
public sealed class ObjectNameComparer : IEqualityComparer<ObjectName>
{
    public static readonly ObjectNameComparer Instance = new();

    public bool Equals(ObjectName? x, ObjectName? y) =>
        x is null ? y is null : y is not null
            && string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(ObjectName obj) =>
        HashCode.Combine(obj.Schema.ToUpperInvariant(), obj.Name.ToUpperInvariant());
}
