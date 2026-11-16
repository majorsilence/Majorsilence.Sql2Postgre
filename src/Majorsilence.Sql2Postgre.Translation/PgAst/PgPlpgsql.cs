using Majorsilence.Sql2Postgre.Translation.Names;

namespace Majorsilence.Sql2Postgre.Translation.PgAst;

// PL/pgSQL: the bodies of functions, procedures and triggers. Names here (variables, labels,
// cursors) are already mapped and unquoted; the writer quotes them where needed.

/// <summary>A statement inside a PL/pgSQL body.</summary>
public abstract record PgPlStatement : PgNode;

// ---- declarations ----

/// <summary>An entry in a block's DECLARE section.</summary>
public abstract record PgDeclaration;

/// <summary><c>name [CONSTANT] type [NOT NULL] [:= default];</c></summary>
public sealed record PgDeclare(string Name, string Type, PgExpr? Default = null, bool Constant = false, bool NotNull = false) : PgDeclaration;

/// <summary>
/// A bound cursor: <c>name [[NO] SCROLL] CURSOR [(arguments)] FOR query;</c>.
/// </summary>
/// <param name="Scroll">SCROLL (true), NO SCROLL (false), or PostgreSQL's default (null).</param>
public sealed record PgDeclareCursor(string Name, PgQuery Query, bool? Scroll = null) : PgDeclaration
{
    public IReadOnlyList<PgField> Arguments { get; init; } = [];
}

/// <summary>One handler of a block's EXCEPTION section: <c>WHEN c1 OR c2 THEN …</c>.</summary>
/// <param name="Conditions">Condition names (<c>OTHERS</c>, <c>unique_violation</c>) or <c>SQLSTATE '23505'</c>, written as given.</param>
public sealed record PgExceptionHandler(IReadOnlyList<string> Conditions, IReadOnlyList<PgPlStatement> Statements);

/// <summary><c>[&lt;&lt;label&gt;&gt;] [DECLARE …] BEGIN … [EXCEPTION …] END [label];</c></summary>
public sealed record PgBlock(IReadOnlyList<PgPlStatement> Statements) : PgPlStatement
{
    public string? Label { get; init; }
    public IReadOnlyList<PgDeclaration> Declarations { get; init; } = [];
    public IReadOnlyList<PgExceptionHandler> Handlers { get; init; } = [];
}

// ---- assignment and control flow ----

/// <summary><c>target := value;</c> The target is a variable, <c>record.field</c> or an array element.</summary>
public sealed record PgAssign(PgExpr Target, PgExpr Value) : PgPlStatement;

public sealed record PgIfBranch(PgExpr Condition, IReadOnlyList<PgPlStatement> Statements);

/// <summary><c>IF … THEN … [ELSIF … THEN …]… [ELSE …] END IF;</c> The first branch is the IF.</summary>
public sealed record PgIf(IReadOnlyList<PgIfBranch> Branches, IReadOnlyList<PgPlStatement>? Else = null) : PgPlStatement;

/// <summary><c>[&lt;&lt;label&gt;&gt;] WHILE condition LOOP … END LOOP [label];</c></summary>
public sealed record PgWhile(PgExpr Condition, IReadOnlyList<PgPlStatement> Statements, string? Label = null) : PgPlStatement;

/// <summary><c>[&lt;&lt;label&gt;&gt;] LOOP … END LOOP [label];</c></summary>
public sealed record PgLoop(IReadOnlyList<PgPlStatement> Statements, string? Label = null) : PgPlStatement;

/// <summary><c>EXIT [label] [WHEN condition];</c></summary>
public sealed record PgExit(string? Label = null, PgExpr? When = null) : PgPlStatement;

/// <summary><c>CONTINUE [label] [WHEN condition];</c></summary>
public sealed record PgContinue(string? Label = null, PgExpr? When = null) : PgPlStatement;

/// <summary>
/// <c>[&lt;&lt;label&gt;&gt;] FOR target IN query LOOP … END LOOP;</c>, or over a dynamic query
/// (<c>FOR target IN EXECUTE command [USING …]</c>) when <see cref="Execute"/> is set.
/// </summary>
/// <param name="Query">The query; null when <see cref="Execute"/> is set.</param>
public sealed record PgForQuery(string Target, PgQuery? Query, IReadOnlyList<PgPlStatement> Statements, string? Label = null) : PgPlStatement
{
    public PgExpr? Execute { get; init; }
    public IReadOnlyList<PgExpr> Using { get; init; } = [];
}

/// <summary><c>[&lt;&lt;label&gt;&gt;] FOR v IN [REVERSE] low .. high [BY step] LOOP … END LOOP;</c></summary>
public sealed record PgForRange(string Variable, PgExpr Low, PgExpr High, IReadOnlyList<PgPlStatement> Statements, string? Label = null) : PgPlStatement
{
    public bool Reverse { get; init; }
    public PgExpr? Step { get; init; }
}

/// <summary><c>RETURN [value];</c></summary>
public sealed record PgReturn(PgExpr? Value = null) : PgPlStatement;

/// <summary><c>RETURN QUERY query;</c> or <c>RETURN QUERY EXECUTE command [USING …];</c></summary>
public sealed record PgReturnQuery(PgQuery? Query) : PgPlStatement
{
    public PgExpr? Execute { get; init; }
    public IReadOnlyList<PgExpr> Using { get; init; } = [];
}

/// <summary><c>RETURN NEXT [value];</c></summary>
public sealed record PgReturnNext(PgExpr? Value = null) : PgPlStatement;

/// <summary>An option of RAISE's USING clause: <c>MESSAGE</c>, <c>DETAIL</c>, <c>HINT</c>, <c>ERRCODE</c>….</summary>
public sealed record PgRaiseOption(string Name, PgExpr Value);

/// <summary>
/// <c>RAISE level 'format', args… [USING option = value, …];</c>. A null <see cref="Level"/> is
/// the bare <c>RAISE;</c> that re-throws inside a handler. The format's <c>%</c> placeholders take
/// <see cref="Arguments"/> in order; the writer quotes it.
/// </summary>
/// <param name="Level"><c>DEBUG</c>, <c>LOG</c>, <c>INFO</c>, <c>NOTICE</c>, <c>WARNING</c> or <c>EXCEPTION</c>.</param>
public sealed record PgRaise(string? Level, string? Format = null) : PgPlStatement
{
    public IReadOnlyList<PgExpr> Arguments { get; init; } = [];
    public IReadOnlyList<PgRaiseOption> Using { get; init; } = [];

    /// <summary>The bare <c>RAISE;</c> that re-throws the error being handled.</summary>
    public static readonly PgRaise Rethrow = new(Level: null);
}

/// <summary><c>ASSERT condition [, message];</c></summary>
public sealed record PgAssert(PgExpr Condition, PgExpr? Message = null) : PgPlStatement;

/// <summary>A query run for its side effects: <c>PERFORM …</c> (SELECT's keyword replaced).</summary>
public sealed record PgPerform(PgQuery Query) : PgPlStatement;

/// <summary><c>EXECUTE command [INTO [STRICT] targets] [USING args];</c></summary>
public sealed record PgExecuteDynamic(PgExpr Command) : PgPlStatement
{
    public IReadOnlyList<PgExpr> Into { get; init; } = [];
    public bool Strict { get; init; }
    public IReadOnlyList<PgExpr> Using { get; init; } = [];
}

/// <summary>One item of GET DIAGNOSTICS: <c>target = ITEM</c>.</summary>
/// <param name="Item"><c>ROW_COUNT</c>, <c>MESSAGE_TEXT</c>, <c>RETURNED_SQLSTATE</c>, <c>PG_EXCEPTION_DETAIL</c>….</param>
public sealed record PgDiagnosticsItem(string Target, string Item);

/// <summary><c>GET [STACKED] DIAGNOSTICS target = item, …;</c></summary>
public sealed record PgGetDiagnostics(IReadOnlyList<PgDiagnosticsItem> Items, bool Stacked = false) : PgPlStatement;

/// <summary>
/// <c>OPEN cursor [(args)];</c> for a bound cursor, <c>OPEN cursor [[NO] SCROLL] FOR query;</c>
/// or <c>OPEN cursor FOR EXECUTE command [USING …];</c> for a refcursor variable.
/// </summary>
public sealed record PgOpenCursor(string Cursor) : PgPlStatement
{
    public IReadOnlyList<PgExpr> Arguments { get; init; } = [];
    public PgQuery? Query { get; init; }
    public PgExpr? Execute { get; init; }
    public IReadOnlyList<PgExpr> Using { get; init; } = [];
    public bool? Scroll { get; init; }
}

/// <summary>
/// <c>FETCH [direction] FROM cursor INTO targets;</c>, or <c>MOVE [direction] FROM cursor;</c>
/// when <see cref="Move"/> is set.
/// </summary>
/// <param name="Direction"><c>NEXT</c>, <c>PRIOR</c>, <c>FIRST</c>, <c>LAST</c>, <c>ABSOLUTE</c>, <c>RELATIVE</c>, <c>FORWARD</c>, <c>BACKWARD</c>; <see cref="Count"/> follows the last four.</param>
public sealed record PgFetch(string Cursor, IReadOnlyList<PgExpr> Into, string Direction = "NEXT") : PgPlStatement
{
    public PgExpr? Count { get; init; }
    public bool Move { get; init; }
}

/// <summary><c>CLOSE cursor;</c></summary>
public sealed record PgClose(string Cursor) : PgPlStatement;

/// <summary><c>COMMIT;</c> or <c>ROLLBACK;</c> (<c>AND CHAIN</c> optional).</summary>
public sealed record PgTransactionControl(bool Commit, bool AndChain = false) : PgPlStatement;

/// <summary>An argument of CALL: positional, or named (<c>name =&gt; value</c>).</summary>
public sealed record PgCallArgument(PgExpr Value, string? Name = null);

/// <summary><c>CALL procedure(args);</c> INOUT arguments must be variables; their values come back into them.</summary>
public sealed record PgCall(PgName Procedure, IReadOnlyList<PgCallArgument> Arguments) : PgPlStatement;

/// <summary>
/// A SQL statement (query, DML, DDL, a comment or an untranslated original) run in the body. With
/// <see cref="Into"/>, a SELECT gets <c>INTO targets</c> after its select list and other statements
/// get it at the end (after RETURNING).
/// </summary>
public sealed record PgSqlStatement(PgStatement Statement) : PgPlStatement
{
    public IReadOnlyList<PgExpr> Into { get; init; } = [];
    public bool Strict { get; init; }
}

/// <summary>The empty statement <c>NULL;</c>.</summary>
public sealed record PgNullStatement : PgPlStatement
{
    public static readonly PgNullStatement Instance = new();
}

// ---- routines ----

/// <summary>A routine parameter.</summary>
/// <param name="Mode"><c>IN</c> (written as nothing), <c>OUT</c>, <c>INOUT</c> or <c>VARIADIC</c>.</param>
public sealed record PgParameter(string Name, string Type, string Mode = "IN", PgExpr? Default = null);

/// <summary>A routine's body.</summary>
public abstract record PgRoutineBody;

/// <summary>A PL/pgSQL body: optional compiler directives (<c>#variable_conflict use_column</c>) and one outer block.</summary>
public sealed record PgPlBody(PgBlock Block) : PgRoutineBody
{
    public IReadOnlyList<string> Directives { get; init; } = [];
}

/// <summary>A <c>LANGUAGE sql</c> body in dollar quotes: statements separated by <c>;</c>.</summary>
public sealed record PgSqlBody(IReadOnlyList<PgStatement> Statements) : PgRoutineBody;

/// <summary>A SQL-standard function body (<c>RETURN expr</c>, no quoting), checked when the function is created.</summary>
public sealed record PgSqlReturnBody(PgExpr Value) : PgRoutineBody;

/// <summary>
/// <c>CREATE [OR REPLACE] FUNCTION | PROCEDURE name(params) [RETURNS …] LANGUAGE … [volatility] AS body</c>.
/// </summary>
/// <param name="Procedure">A procedure (no RETURNS) rather than a function.</param>
public sealed record PgCreateRoutine(PgName Name, bool Procedure, IReadOnlyList<PgParameter> Parameters, PgRoutineBody Body) : PgStatement
{
    public bool OrReplace { get; init; }

    /// <summary>The return type (<c>integer</c>, <c>trigger</c>, <c>SETOF x</c>); ignored when <see cref="ReturnsTable"/> is set.</summary>
    public string? Returns { get; init; }

    /// <summary>The columns of <c>RETURNS TABLE (…)</c>.</summary>
    public IReadOnlyList<PgField> ReturnsTable { get; init; } = [];

    /// <summary><c>IMMUTABLE</c>, <c>STABLE</c>, <c>VOLATILE</c>, or null to leave PostgreSQL's default (VOLATILE).</summary>
    public string? Volatility { get; init; }

    /// <summary>The language; <c>plpgsql</c> for a <see cref="PgPlBody"/>, <c>sql</c> otherwise.</summary>
    public string Language => Body is PgPlBody ? "plpgsql" : "sql";
}
