using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Translation.Semantics;

/// <summary>A column a relation offers, with its SQL Server type when known.</summary>
public sealed record ColumnInfo(string Name, SqlServerType? Type);

/// <summary>
/// Something rows come from: a table, a view, a table-valued function's result, a CTE, a derived
/// table, a table variable or a temp table. <see cref="Name"/> is null for the unnamed kinds.
/// </summary>
public sealed record RelationInfo(ObjectName? Name, ObjectKind Kind, IReadOnlyList<ColumnInfo> Columns)
{
    public ColumnInfo? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Name">The name without its <c>@</c>.</param>
public sealed record ParameterInfo(string Name, SqlServerType Type, bool IsOutput, bool HasDefault);

/// <summary>A function or procedure's signature, read from its CREATE statement.</summary>
/// <param name="ReturnType">A scalar function's return type.</param>
/// <param name="ResultColumns">A table-valued function's result columns.</param>
public sealed record RoutineInfo(ObjectName Name, ObjectKind Kind, IReadOnlyList<ParameterInfo> Parameters,
    SqlServerType? ReturnType, IReadOnlyList<ColumnInfo>? ResultColumns)
{
    public bool IsTableValued => ResultColumns is not null;
}

/// <summary>One relation visible in a query, under the name a reference may qualify a column with.</summary>
/// <param name="Alias">The alias, or the relation's own name when it has none.</param>
public sealed record Binding(string Alias, RelationInfo Relation);

/// <summary>A column reference resolved to the relation it comes from.</summary>
/// <param name="Depth">0 when found in the innermost scope; more for an outer (correlated) scope.</param>
public sealed record ResolvedColumn(Binding Binding, ColumnInfo Column, int Depth);

/// <summary>
/// The names visible at one point of a statement: the relations of the current FROM clause, the
/// CTEs in force, and the variables and parameters of the routine, with the enclosing scope behind
/// it for correlated references. Resolved inside out, as SQL Server resolves them.
/// </summary>
public sealed class Scope(Scope? parent = null)
{
    private readonly List<Binding> _bindings = [];
    private readonly Dictionary<string, RelationInfo> _ctes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SqlServerType?> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelationInfo> _tempTables = new(StringComparer.OrdinalIgnoreCase);

    public Scope? Parent { get; } = parent;

    public IReadOnlyList<Binding> Bindings => _bindings;

    public Scope Child() => new(this);

    public void Bind(Binding binding) => _bindings.Add(binding);

    public void DefineCte(string name, RelationInfo relation) => _ctes[name] = relation;

    /// <summary>A variable or parameter, with or without its <c>@</c>.</summary>
    public void DefineVariable(string name, SqlServerType? type) => _variables[name.TrimStart('@')] = type;

    /// <summary>A table variable (<c>@t</c>) or temp table (<c>#t</c>) by its full name.</summary>
    public void DefineTable(string name, RelationInfo relation) => _tempTables[name] = relation;

    public RelationInfo? Cte(string name) =>
        _ctes.TryGetValue(name, out var r) ? r : Parent?.Cte(name);

    public RelationInfo? Table(string name) =>
        _tempTables.TryGetValue(name, out var r) ? r : Parent?.Table(name);

    public bool TryVariable(string name, out SqlServerType? type)
    {
        for (var s = this; s is not null; s = s.Parent)
            if (s._variables.TryGetValue(name.TrimStart('@'), out type))
                return true;
        type = null;
        return false;
    }

    /// <summary>
    /// A column by its parts (<c>col</c>, <c>alias.col</c>, <c>schema.table.col</c>): qualified, the
    /// innermost scope whose binding matches the qualifier; unqualified, the innermost scope where
    /// exactly one binding has the column. Null when no scope has it, or it is ambiguous.
    /// </summary>
    public ResolvedColumn? ResolveColumn(IReadOnlyList<string> parts)
    {
        string column = parts[^1];
        int depth = 0;
        for (var s = this; s is not null; s = s.Parent, depth++)
        {
            if (parts.Count >= 2)
            {
                string qualifier = parts[^2];
                string? schema = parts.Count >= 3 ? parts[^3] : null;
                var binding = s._bindings.FirstOrDefault(b => Matches(b, qualifier, schema));
                if (binding?.Relation.Column(column) is { } c)
                    return new ResolvedColumn(binding, c, depth);
                if (binding is not null)
                    return null;
            }
            else
            {
                var matches = s._bindings.Where(b => b.Relation.Column(column) is not null).ToList();
                if (matches.Count == 1)
                    return new ResolvedColumn(matches[0], matches[0].Relation.Column(column)!, depth);
                if (matches.Count > 1)
                    return null;
            }
        }
        return null;
    }

    /// <summary>The binding a qualifier names (alias, or table name with an optional schema).</summary>
    public Binding? ResolveQualifier(string qualifier, string? schema = null)
    {
        for (var s = this; s is not null; s = s.Parent)
            if (s._bindings.FirstOrDefault(b => Matches(b, qualifier, schema)) is { } b)
                return b;
        return null;
    }

    private static bool Matches(Binding b, string qualifier, string? schema) =>
        string.Equals(b.Alias, qualifier, StringComparison.OrdinalIgnoreCase)
        && (schema is null || b.Relation.Name is null || string.Equals(b.Relation.Name.Schema, schema, StringComparison.OrdinalIgnoreCase));
}
