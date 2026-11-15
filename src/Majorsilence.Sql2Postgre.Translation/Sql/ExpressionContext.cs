using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Where an expression is being translated: the names in scope, the schema unqualified names
/// resolve in, the object diagnostics belong to, and what the translation needs (extensions,
/// mssql_compat helpers). Child contexts share the collections, so a whole statement reports once.
/// </summary>
public sealed class ExpressionContext
{
    public ExpressionContext(Scope scope, string? schema, ObjectName? owner)
        : this(scope, schema, owner, [], new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal))
    {
    }

    private ExpressionContext(Scope scope, string? schema, ObjectName? owner, List<Diagnostic> diagnostics,
        SortedSet<string> extensions, SortedSet<string> compat)
    {
        Scope = scope;
        Schema = schema;
        Owner = owner;
        Diagnostics = diagnostics;
        Extensions = extensions;
        CompatFunctions = compat;
    }

    public Scope Scope { get; }

    /// <summary>The schema unqualified names resolve in first (then <c>dbo</c>).</summary>
    public string? Schema { get; }

    public ObjectName? Owner { get; }

    public List<Diagnostic> Diagnostics { get; }

    public SortedSet<string> Extensions { get; }

    /// <summary>The mssql_compat helpers the translation calls.</summary>
    public SortedSet<string> CompatFunctions { get; }

    /// <summary>Write column references with their table qualifier when the T-SQL has one (false for a table's own expressions).</summary>
    public bool QualifyColumns { get; init; } = true;

    /// <summary>The expression must be immutable (a generated column): prefer immutable spellings (|| over concat()).</summary>
    public bool RequireImmutable { get; init; }

    /// <summary>Inside a FOR JSON query: a nested FOR JSON subquery is JSON, not a string.</summary>
    public bool InsideJson { get; init; }

    /// <summary>In an OUTPUT clause: <c>inserted</c> and <c>deleted</c> are written as RETURNING's <c>new</c> and <c>old</c>.</summary>
    public bool ReturningPseudoTables { get; init; }

    public ExpressionContext With(Scope scope) =>
        new(scope, Schema, Owner, Diagnostics, Extensions, CompatFunctions)
        {
            QualifyColumns = QualifyColumns, RequireImmutable = RequireImmutable, InsideJson = InsideJson, ReturningPseudoTables = ReturningPseudoTables,
        };

    public ExpressionContext WithInsideJson() =>
        new(Scope, Schema, Owner, Diagnostics, Extensions, CompatFunctions)
        {
            QualifyColumns = QualifyColumns, RequireImmutable = RequireImmutable, InsideJson = true, ReturningPseudoTables = ReturningPseudoTables,
        };

    public ExpressionContext WithReturning(Scope scope) =>
        new(scope, Schema, Owner, Diagnostics, Extensions, CompatFunctions)
        {
            QualifyColumns = true, RequireImmutable = RequireImmutable, InsideJson = InsideJson, ReturningPseudoTables = true,
        };

    public void Report(string code, Severity severity, string message) =>
        Diagnostics.Add(new Diagnostic(code, severity, Owner, message));
}

/// <summary>A construct the translator does not handle; the caller keeps the original T-SQL as a comment.</summary>
public sealed class UntranslatableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
