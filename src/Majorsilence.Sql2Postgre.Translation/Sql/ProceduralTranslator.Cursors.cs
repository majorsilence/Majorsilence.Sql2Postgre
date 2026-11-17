using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Cursors. The common loop — <c>OPEN c; FETCH NEXT FROM c INTO @a, @b; WHILE @@FETCH_STATUS = 0
/// BEGIN … FETCH NEXT FROM c INTO @a, @b; END</c> — becomes <c>FOR … IN query LOOP … END LOOP</c>.
/// Any other use is an unbound <c>refcursor</c> variable <c>cur_name</c>, opened with the query
/// when T-SQL opens it (its variables are read then, as in T-SQL). FETCH reads into helper
/// variables, and the T-SQL variables take the row only when there is one: PL/pgSQL would set
/// them to NULL past the end, and T-SQL leaves them as they were.
/// </summary>
public sealed partial class ProceduralTranslator
{
    private const string FetchStatusVariable = "sql2pg_fetch_status";

    /// <summary>A T-SQL cursor: its refcursor variable and the query it was last declared with.</summary>
    internal sealed class CursorInfo(string variable)
    {
        public string Variable { get; } = variable;

        /// <summary>The refcursor variable is declared (only once OPEN, FETCH or CLOSE needs it).</summary>
        public bool Declared { get; set; }
        public SelectStatement? Query { get; set; }
        public bool Scroll { get; set; }

        /// <summary>The FOR loop replaced its OPEN … FETCH loop: its CLOSE has nothing to close.</summary>
        public bool Looped { get; set; }
    }

    /// <summary>What a body does with each cursor, found before it is translated.</summary>
    internal sealed class CursorUse
    {
        public int Fetches { get; set; }
        public bool Scrolls { get; set; }
        public bool CurrentOf { get; set; }
    }

    private static string CursorKey(CursorId id) =>
        id.Name.ValueExpression is VariableReference v ? v.Name : id.Name.Identifier?.Value ?? id.Name.Value;

    private sealed class CursorFinder : TSqlFragmentVisitor
    {
        public Dictionary<string, CursorUse> Uses { get; } = new(StringComparer.OrdinalIgnoreCase);

        private CursorUse Use(CursorId id)
        {
            string key = CursorKey(id);
            if (!Uses.TryGetValue(key, out var use))
                Uses[key] = use = new CursorUse();
            return use;
        }

        public override void Visit(FetchCursorStatement node)
        {
            var use = Use(node.Cursor);
            use.Fetches++;
            if (node.FetchType?.Orientation is not (null or FetchOrientation.None or FetchOrientation.Next))
                use.Scrolls = true;
        }

        public override void Visit(WhereClause node)
        {
            if (node.Cursor is { } cursor)
                Use(cursor).CurrentOf = true;
        }
    }

    private CursorInfo CursorVariable(string key, BodyState st)
    {
        if (!st.Cursors.TryGetValue(key, out var info))
        {
            info = new CursorInfo(UniqueName("cur_" + _names.Alias(key.TrimStart('@')), st));
            st.Cursors[key] = info;
        }
        return info;
    }

    /// <summary>The cursor's refcursor variable, declared when first needed.</summary>
    private static string CursorRef(CursorInfo info, BodyState st)
    {
        if (!info.Declared)
        {
            info.Declared = true;
            st.Declarations.Add(new PgDeclare(info.Variable, "refcursor"));
        }
        return info.Variable;
    }

    /// <summary>DECLARE c CURSOR … FOR query, or SET @c = CURSOR … FOR query: the query is kept for OPEN.</summary>
    private IEnumerable<PgPlStatement> DeclareCursor(string key, CursorDefinition definition, TSqlFragment source, BodyState st)
    {
        var options = definition.Options.Select(o => o.OptionKind).ToHashSet();
        if (options.Contains(CursorOptionKind.Global))
            throw ExpressionTranslator.Untranslatable(source,
                "A GLOBAL cursor, which outlives the code that declares it (PL/pgSQL cursors belong to the routine that opens them),", DiagnosticCodes.GlobalCursor);
        var info = CursorVariable(key, st);
        info.Query = definition.Select;
        info.Scroll = options.Contains(CursorOptionKind.Scroll) || options.Contains(CursorOptionKind.Static)
                      || options.Contains(CursorOptionKind.Keyset) || options.Contains(CursorOptionKind.Dynamic)
                      || st.CursorUses.GetValueOrDefault(key)?.Scrolls == true;
        return [];
    }

    private CursorInfo Cursor(CursorId id, TSqlFragment source, BodyState st)
    {
        if (id.IsGlobal)
            throw ExpressionTranslator.Untranslatable(source, "A GLOBAL cursor", DiagnosticCodes.GlobalCursor);
        return st.Cursors.GetValueOrDefault(CursorKey(id))
               ?? throw ExpressionTranslator.Untranslatable(source, "A cursor not declared in this code", DiagnosticCodes.ProceduralStatement);
    }

    /// <summary>The cursor's query; FOR UPDATE when the body changes its rows with WHERE CURRENT OF.</summary>
    private PgQuery CursorQuery(string key, CursorInfo info, TSqlFragment source, BodyState st)
    {
        if (info.Query is null)
            throw ExpressionTranslator.Untranslatable(source, "OPEN of a cursor variable no CURSOR FOR query was assigned to", DiagnosticCodes.ProceduralStatement);
        var query = ((PgQueryStatement)_queries.Statement(info.Query, st.C)).Query;
        return st.CursorUses.GetValueOrDefault(key)?.CurrentOf == true && query is PgSelect select ? select with { ForUpdate = true } : query;
    }

    private IEnumerable<PgPlStatement> OpenCursor(OpenCursorStatement open, BodyState st)
    {
        var info = Cursor(open.Cursor, open, st);
        info.Looped = false;
        return [new PgOpenCursor(CursorRef(info, st)) { Query = CursorQuery(CursorKey(open.Cursor), info, open, st), Scroll = info.Scroll ? true : null }];
    }

    /// <summary>The helper variables a FETCH reads into: one per target, of the target's type, reused across FETCHes.</summary>
    private List<PgColumnRef> FetchHelpers(IList<VariableReference> into, BodyState st)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var helpers = new List<PgColumnRef>();
        foreach (var v in into)
        {
            string name = v.Name.TrimStart('@');
            string pgType = st.Variables.TryGetValue(name, out var variable) ? variable.PgType
                : Target(v, st).Type is { } type ? _x.Types.Map(type with { UserTypeName = null }, st.C.Owner).PgType
                : throw ExpressionTranslator.Untranslatable(v, $"FETCH into {v.Name}, whose type is unknown,");
            int n = used.GetValueOrDefault(pgType);
            used[pgType] = n + 1;
            if (!st.FetchHelpers.TryGetValue(pgType, out var pool))
                st.FetchHelpers[pgType] = pool = [];
            if (n == pool.Count)
                pool.Add(Helper($"sql2pg_fetch{st.FetchHelpers.Values.Sum(x => x.Count) + 1}", pgType, st).Name);
            helpers.Add(new PgColumnRef(pool[n]));
        }
        return helpers;
    }

    private List<PgPlStatement> TakeRow(IList<VariableReference> into, IReadOnlyList<PgColumnRef> helpers, BodyState st)
    {
        var assignments = new List<PgPlStatement>();
        for (int k = 0; k < into.Count; k++)
        {
            assignments.Add(new PgAssign(Target(into[k], st).Target, helpers[k]));
            st.Assigned.Add(into[k].Name.TrimStart('@'));
        }
        return assignments;
    }

    private IEnumerable<PgPlStatement> Fetch(FetchCursorStatement fetch, BodyState st)
    {
        var info = Cursor(fetch.Cursor, fetch, st);
        if (fetch.IntoVariables.Count == 0)
            throw ExpressionTranslator.Untranslatable(fetch, "FETCH without INTO (the row is a result set)", DiagnosticCodes.ProceduralStatement);
        var (direction, count) = fetch.FetchType?.Orientation switch
        {
            FetchOrientation.Prior => ("PRIOR", null),
            FetchOrientation.First => ("FIRST", null),
            FetchOrientation.Last => ("LAST", null),
            FetchOrientation.Absolute => ("ABSOLUTE", _x.Scalar(fetch.FetchType.RowOffset, st.C)),
            FetchOrientation.Relative => ("RELATIVE", _x.Scalar(fetch.FetchType.RowOffset, st.C)),
            _ => ("NEXT", (PgExpr?)null),
        };
        var helpers = FetchHelpers(fetch.IntoVariables, st);
        var found = TakeRow(fetch.IntoVariables, helpers, st);
        var notFound = new List<PgPlStatement>();
        if (st.ReadsFetchStatus)
        {
            found.Add(new PgAssign(Helper(FetchStatusVariable, "integer", st), new PgLiteral("0")));
            notFound.Add(new PgAssign(Helper(FetchStatusVariable, "integer", st), new PgLiteral("-1")));
        }
        if (st.ReadsRowCount)
        {
            found.Add(new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("1")));
            notFound.Add(new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("0")));
        }
        return
        [
            new PgFetch(CursorRef(info, st), helpers, direction) { Count = count },
            new PgIf([new PgIfBranch(new PgKeyword("FOUND"), found)], notFound.Count > 0 ? notFound : null),
        ];
    }

    private IEnumerable<PgPlStatement> CloseCursor(CloseCursorStatement close, BodyState st)
    {
        var info = Cursor(close.Cursor, close, st);
        if (info.Looped)
        {
            info.Looped = false;
            return [];
        }
        return [new PgClose(CursorRef(info, st))];
    }

    /// <summary>
    /// <c>OPEN c; FETCH NEXT FROM c INTO vars; WHILE @@FETCH_STATUS = 0 BEGIN … FETCH NEXT FROM c
    /// INTO vars; END</c> at <paramref name="i"/> as a FOR loop, when the cursor is used nowhere
    /// else (no other FETCH, no WHERE CURRENT OF) and the body has no CONTINUE (which in T-SQL
    /// would skip the FETCH).
    /// </summary>
    private List<PgPlStatement>? TryCursorLoop(IList<TSqlStatement> statements, int i, BodyState st)
    {
        try
        {
            return CursorLoop(statements, i, st, out var loop) ? loop : null;
        }
        catch (UntranslatableException)
        {
            // The statements are translated one by one instead, and report what fails.
            return null;
        }
    }

    private bool CursorLoop(IList<TSqlStatement> statements, int i, BodyState st, out List<PgPlStatement> loop)
    {
        loop = [];
        if (i + 2 >= statements.Count
            || statements[i] is not OpenCursorStatement { Cursor.IsGlobal: false } open
            || statements[i + 1] is not FetchCursorStatement first
            || statements[i + 2] is not WhileStatement w
            || !IsFetchStatusZero(w.Predicate))
            return false;
        string key = CursorKey(open.Cursor);
        if (!st.Cursors.TryGetValue(key, out var info) || info.Query is null
            || st.CursorUses.GetValueOrDefault(key) is not { Fetches: 2, CurrentOf: false }
            || !IsNextInto(first, key))
            return false;
        var body = w.Statement is BeginEndBlockStatement block ? block.StatementList.Statements.ToList() : [w.Statement];
        if (body.LastOrDefault() is not FetchCursorStatement last || !IsNextInto(last, key)
            || !last.IntoVariables.Select(v => v.Name).SequenceEqual(first.IntoVariables.Select(v => v.Name), StringComparer.OrdinalIgnoreCase)
            || Contains<ContinueStatement>(body))
            return false;
        body.RemoveAt(body.Count - 1);

        var query = CursorQuery(key, info, open, st);
        var helpers = FetchHelpers(first.IntoVariables, st);
        var statementsInLoop = TakeRow(first.IntoVariables, helpers, st);
        if (st.ReadsFetchStatus)
            statementsInLoop.Add(new PgAssign(Helper(FetchStatusVariable, "integer", st), new PgLiteral("0")));
        st.LoopDepth++;
        st.Nesting++;
        try
        {
            statementsInLoop.AddRange(List(body, st));
        }
        finally
        {
            st.LoopDepth--;
            st.Nesting--;
        }
        loop.Add(new PgForQuery(helpers[0].Name, query, statementsInLoop) { Targets = helpers.Select(h => h.Name).ToList() });
        if (st.ReadsFetchStatus)
            loop.Add(new PgAssign(Helper(FetchStatusVariable, "integer", st), new PgLiteral("-1")));
        info.Looped = true;
        return true;
    }

    private static bool IsNextInto(FetchCursorStatement fetch, string key) =>
        fetch.FetchType?.Orientation is null or FetchOrientation.None or FetchOrientation.Next
        && fetch.IntoVariables.Count > 0 && !fetch.Cursor.IsGlobal
        && string.Equals(CursorKey(fetch.Cursor), key, StringComparison.OrdinalIgnoreCase);

    private static bool IsFetchStatusZero(BooleanExpression e) => e switch
    {
        BooleanParenthesisExpression p => IsFetchStatusZero(p.Expression),
        BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } c =>
            IsFetchStatus(c.FirstExpression) && IsZero(c.SecondExpression) || IsFetchStatus(c.SecondExpression) && IsZero(c.FirstExpression),
        _ => false,
    };

    private static bool IsFetchStatus(ScalarExpression e) =>
        Unwrap(e) is GlobalVariableExpression g && g.Name.Equals("@@FETCH_STATUS", StringComparison.OrdinalIgnoreCase);

    private static bool IsZero(ScalarExpression e) => Unwrap(e) is IntegerLiteral { Value: "0" };
}
