using Majorsilence.Sql2Postgre.Translation.Names;

namespace Majorsilence.Sql2Postgre.Translation.PgAst;

/// <summary>A query: a SELECT, a set operation, or VALUES.</summary>
public abstract record PgQuery : PgNode;

/// <summary>One item of a select list: an expression with an optional alias, or <c>*</c> / <c>alias.*</c>.</summary>
public sealed record PgTarget(PgExpr? Expression, string? Alias = null, bool Star = false, string? StarQualifier = null)
{
    public static PgTarget All(string? qualifier = null) => new(null, null, true, qualifier);
}

public sealed record PgCte(string Name, IReadOnlyList<string> Columns, PgQuery Query);

/// <summary>A GROUP BY item: an expression, or ROLLUP / CUBE / GROUPING SETS of item lists (an empty list is <c>()</c>).</summary>
public abstract record PgGroupItem;

public sealed record PgGroupExpr(PgExpr Expression) : PgGroupItem;

/// <param name="Kind"><c>ROLLUP</c>, <c>CUBE</c> or <c>GROUPING SETS</c>.</param>
public sealed record PgGroupSets(string Kind, IReadOnlyList<IReadOnlyList<PgExpr>> Sets) : PgGroupItem;

public sealed record PgSelect : PgQuery
{
    public IReadOnlyList<PgCte> With { get; init; } = [];
    public bool Recursive { get; init; }
    public bool Distinct { get; init; }
    public IReadOnlyList<PgTarget> Targets { get; init; } = [];
    public IReadOnlyList<PgFromItem> From { get; init; } = [];
    public PgExpr? Where { get; init; }
    public IReadOnlyList<PgGroupItem> GroupBy { get; init; } = [];
    public PgExpr? Having { get; init; }

    /// <summary>The WINDOW clause.</summary>
    public IReadOnlyList<PgNamedWindow> Windows { get; init; } = [];
    public IReadOnlyList<PgSortKey> OrderBy { get; init; } = [];
    public PgExpr? Limit { get; init; }
    public PgExpr? Offset { get; init; }

    /// <summary>FETCH FIRST <see cref="Limit"/> ROWS WITH TIES instead of LIMIT.</summary>
    public bool WithTies { get; init; }

    /// <summary><c>FOR UPDATE</c> (a cursor whose rows are changed with WHERE CURRENT OF).</summary>
    public bool ForUpdate { get; init; }
}

/// <param name="Operator"><c>UNION</c>, <c>UNION ALL</c>, <c>EXCEPT</c>, <c>INTERSECT</c>.</param>
public sealed record PgSetOperation(string Operator, PgQuery Left, PgQuery Right) : PgQuery
{
    public IReadOnlyList<PgCte> With { get; init; } = [];
    public bool Recursive { get; init; }
    public IReadOnlyList<PgSortKey> OrderBy { get; init; } = [];
    public PgExpr? Limit { get; init; }
    public PgExpr? Offset { get; init; }
}

public sealed record PgValues(IReadOnlyList<IReadOnlyList<PgExpr>> Rows) : PgQuery;

// ---- FROM items ----

public abstract record PgFromItem;

/// <param name="Name">The table, view or CTE (a CTE or temp table has an empty schema).</param>
public sealed record PgTableRef(PgName Name, string? Alias = null) : PgFromItem
{
    /// <summary><c>TABLESAMPLE SYSTEM (percent) [REPEATABLE (seed)]</c> after the alias.</summary>
    public PgTableSample? Sample { get; init; }
}

public sealed record PgTableSample(PgExpr Percent, PgExpr? Seed);

public sealed record PgSubqueryRef(PgQuery Query, string Alias, IReadOnlyList<string> Columns, bool Lateral = false) : PgFromItem;

/// <summary>A column of XMLTABLE: <c>name type PATH 'path' [DEFAULT value]</c>.</summary>
public sealed record PgXmlTableColumn(string Name, string Type, string Path, PgExpr? Default = null);

/// <summary>
/// <c>XMLTABLE([XMLNAMESPACES('uri' AS prefix, …),] 'row path' PASSING (document) COLUMNS …) [AS alias]</c>.
/// Like other table functions in FROM, it may use the FROM items before it.
/// </summary>
public sealed record PgXmlTable(IReadOnlyList<(string Prefix, string Uri)> Namespaces, string RowPath, PgExpr Document,
    IReadOnlyList<PgXmlTableColumn> Columns, string? Alias = null) : PgFromItem;

public sealed record PgFunctionRef(string Name, IReadOnlyList<PgExpr> Arguments, string? Alias, IReadOnlyList<string> Columns, bool Lateral = false) : PgFromItem;

/// <param name="Kind"><c>JOIN</c>, <c>LEFT JOIN</c>, <c>RIGHT JOIN</c>, <c>FULL JOIN</c>, <c>CROSS JOIN</c>.</param>
/// <param name="On">The join condition; null for CROSS JOIN.</param>
public sealed record PgJoin(string Kind, PgFromItem Left, PgFromItem Right, PgExpr? On) : PgFromItem;

// ---- expressions over queries ----

public sealed record PgSubquery(PgQuery Query) : PgExpr;

public sealed record PgExists(PgQuery Query, bool Negated = false) : PgExpr;

public sealed record PgInSubquery(PgExpr Operand, PgQuery Query, bool Negated = false) : PgExpr;

/// <param name="Quantifier"><c>ANY</c> or <c>ALL</c>.</param>
public sealed record PgQuantified(PgExpr Operand, string Operator, string Quantifier, PgQuery Query) : PgExpr;

// ---- statements ----

/// <summary><c>CREATE [MATERIALIZED] VIEW name [(columns)] AS query [WITH CHECK OPTION]</c>.</summary>
public sealed record PgCreateView(PgName Name, IReadOnlyList<string> Columns, PgQuery Query, bool Materialized = false, bool CheckOption = false) : PgStatement;

/// <summary>A query run as a statement.</summary>
public sealed record PgQueryStatement(PgQuery Query) : PgStatement;

/// <summary><c>CREATE [TEMP] TABLE name AS query</c> (T-SQL's SELECT … INTO).</summary>
public sealed record PgCreateTableAs(PgName Name, PgQuery Query, bool Temporary) : PgStatement;

public sealed record PgAssignment(string Column, PgExpr Value);

public sealed record PgInsert(PgName Table, IReadOnlyList<string> Columns, PgQuery? Source) : PgStatement
{
    public IReadOnlyList<PgCte> With { get; init; } = [];

    /// <summary>INSERT … DEFAULT VALUES (when <see cref="Source"/> is null).</summary>
    public bool DefaultValues => Source is null;

    public IReadOnlyList<PgTarget> Returning { get; init; } = [];
}

public sealed record PgUpdate(PgName Table, string? Alias, IReadOnlyList<PgAssignment> Set) : PgStatement
{
    public IReadOnlyList<PgCte> With { get; init; } = [];
    public IReadOnlyList<PgFromItem> From { get; init; } = [];
    public PgExpr? Where { get; init; }
    public IReadOnlyList<PgTarget> Returning { get; init; } = [];
}

public sealed record PgDelete(PgName Table, string? Alias) : PgStatement
{
    public IReadOnlyList<PgCte> With { get; init; } = [];
    public IReadOnlyList<PgFromItem> Using { get; init; } = [];
    public PgExpr? Where { get; init; }
    public IReadOnlyList<PgTarget> Returning { get; init; } = [];
}

/// <param name="Condition"><c>MATCHED</c>, <c>NOT MATCHED</c> (by target) or <c>NOT MATCHED BY SOURCE</c>.</param>
public sealed record PgMergeWhen(string Condition, PgExpr? And, PgMergeAction Action);

public abstract record PgMergeAction;

public sealed record PgMergeUpdate(IReadOnlyList<PgAssignment> Set) : PgMergeAction;

public sealed record PgMergeDelete : PgMergeAction;

public sealed record PgMergeInsert(IReadOnlyList<string> Columns, IReadOnlyList<PgExpr>? Values) : PgMergeAction;

public sealed record PgMerge(PgName Table, string? Alias, PgFromItem Source, PgExpr On, IReadOnlyList<PgMergeWhen> Whens) : PgStatement
{
    public IReadOnlyList<PgCte> With { get; init; } = [];
    public IReadOnlyList<PgTarget> Returning { get; init; } = [];
}

/// <summary>
/// An INSERT, UPDATE, DELETE or MERGE with RETURNING used as a query: only as a CTE
/// (<c>WITH ins AS (INSERT … RETURNING id) SELECT …</c>).
/// </summary>
public sealed record PgModifyingQuery(PgStatement Statement) : PgQuery;
