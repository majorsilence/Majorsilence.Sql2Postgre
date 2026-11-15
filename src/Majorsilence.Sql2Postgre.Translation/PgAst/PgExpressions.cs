namespace Majorsilence.Sql2Postgre.Translation.PgAst;

/// <summary>
/// The PostgreSQL the translator emits, as a small immutable tree. Names in it are already
/// mapped (unquoted); the writer quotes them and adds the parentheses precedence needs, so
/// translators never deal in text.
/// </summary>
public abstract record PgNode;

public abstract record PgExpr : PgNode;

/// <summary>A literal already in PostgreSQL syntax: <c>'it''s'</c>, <c>42</c>, <c>NULL</c>, <c>true</c>.</summary>
public sealed record PgLiteral(string Sql) : PgExpr
{
    public static readonly PgLiteral Null = new("NULL");
    public static readonly PgLiteral True = new("true");
    public static readonly PgLiteral False = new("false");

    public static PgLiteral String(string value) => new($"'{value.Replace("'", "''")}'");
}

/// <summary>A keyword that is a value on its own, written without parentheses: <c>localtimestamp</c>, <c>current_user</c>.</summary>
public sealed record PgKeyword(string Sql) : PgExpr;

/// <summary>A column, optionally qualified by a table or alias (both mapped names).</summary>
public sealed record PgColumnRef(string Name, string? Qualifier = null) : PgExpr;

/// <summary><c>-x</c>, <c>+x</c>, <c>~x</c>, <c>NOT x</c>.</summary>
public sealed record PgUnary(string Operator, PgExpr Operand) : PgExpr;

/// <summary>An infix operator: arithmetic, <c>||</c>, comparisons, <c>AND</c>, <c>OR</c>, bitwise.</summary>
public sealed record PgBinary(string Operator, PgExpr Left, PgExpr Right) : PgExpr;

public sealed record PgIsNull(PgExpr Operand, bool Negated = false) : PgExpr;

public sealed record PgInList(PgExpr Operand, IReadOnlyList<PgExpr> Values, bool Negated = false) : PgExpr;

public sealed record PgBetween(PgExpr Operand, PgExpr Low, PgExpr High, bool Negated = false) : PgExpr;

/// <param name="Similar">SIMILAR TO (a regular-expression pattern, for T-SQL's <c>[ ]</c> character classes) instead of LIKE.</param>
public sealed record PgLike(PgExpr Operand, PgExpr Pattern, PgExpr? Escape = null, bool Negated = false, bool Similar = false) : PgExpr;

public sealed record PgWhen(PgExpr Condition, PgExpr Result);

/// <summary><c>CASE [operand] WHEN … THEN … [ELSE …] END</c>.</summary>
public sealed record PgCase(PgExpr? Operand, IReadOnlyList<PgWhen> Whens, PgExpr? Else) : PgExpr;

/// <summary><c>CAST(x AS type)</c>; the type is written verbatim.</summary>
public sealed record PgCast(PgExpr Operand, string Type) : PgExpr;

/// <summary>A function call; the name is written verbatim (it may be schema-qualified).</summary>
/// <param name="Star">The argument is <c>*</c> (<c>count(*)</c>).</param>
public sealed record PgFunctionCall(string Name, IReadOnlyList<PgExpr> Arguments) : PgExpr
{
    public bool Star { get; init; }

    /// <summary>An aggregate over distinct values: <c>count(DISTINCT x)</c>.</summary>
    public bool Distinct { get; init; }

    /// <summary>An ordered aggregate's ORDER BY, inside the parentheses: <c>string_agg(x, ',' ORDER BY y)</c>.</summary>
    public IReadOnlyList<PgSortKey> OrderBy { get; init; } = [];

    /// <summary>A window function's OVER clause.</summary>
    public PgWindow? Over { get; init; }
}

/// <param name="NullsFirst">NULLS FIRST (true), NULLS LAST (false), or PostgreSQL's default (null).</param>
public sealed record PgSortKey(PgExpr Expression, bool Descending = false, bool? NullsFirst = null);

/// <param name="Frame">The frame clause as PostgreSQL writes it (<c>ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW</c>), or null.</param>
public sealed record PgWindow(IReadOnlyList<PgExpr> PartitionBy, IReadOnlyList<PgSortKey> OrderBy, string? Frame = null);

/// <summary><c>x AT TIME ZONE zone</c>.</summary>
public sealed record PgAtTimeZone(PgExpr Operand, PgExpr Zone) : PgExpr;

/// <summary>
/// SQL with keyword-shaped syntax no function call expresses (<c>extract(year FROM {0})</c>,
/// <c>overlay({0} placing {1} from {2} for {3})</c>). Each <c>{n}</c> is replaced by argument n,
/// always parenthesized, so the template never depends on precedence.
/// </summary>
public sealed record PgTemplate(string Template, IReadOnlyList<PgExpr> Arguments) : PgExpr;
