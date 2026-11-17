using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// TRY … CATCH: a block with an <c>EXCEPTION WHEN OTHERS</c> handler, which undoes the TRY's
/// work when it fails, as SQL Server's transaction rollback in CATCH would. The ERROR_*()
/// functions read helper variables the handler fills with <c>GET STACKED DIAGNOSTICS</c>.
/// PL/pgSQL cannot end a transaction inside a block that has a handler, so a TRY's
/// <c>BEGIN TRAN</c> commits the work before the block, and a COMMIT ending the TRY runs right
/// after it; the handler's own COMMIT and ROLLBACK are allowed.
/// </summary>
public sealed partial class ProceduralTranslator
{
    /// <summary>The CATCH block being translated: its helper variables, and whether a transaction may be open in it.</summary>
    private sealed record ErrorFrame(string Suffix, bool TransactionMayBeOpen);

    private static string ErrorHelper(string name, ErrorFrame frame) => $"sql2pg_error_{name}{frame.Suffix}";

    /// <summary>T-SQL error numbers for the SQLSTATEs code most often tests; anything else is 50000.</summary>
    private static readonly (string SqlState, int Number)[] ErrorNumbers =
    [
        ("23505", 2627), // unique or primary key violation
        ("23503", 547), // foreign key violation
        ("23514", 547), // check violation
        ("23502", 515), // NULL into a NOT NULL column
        ("22012", 8134), // division by zero
        ("22003", 8115), // arithmetic overflow
        ("22001", 8152), // string or binary data would be truncated
        ("22P02", 245), // conversion failed
        ("22007", 241), // date/time conversion failed
        ("22008", 242), // date/time out of range
        ("40P01", 1205), // deadlock victim
        ("40001", 1205), // serialization failure
    ];

    private IEnumerable<PgPlStatement> TryCatch(TryCatchStatement tc, BodyState st)
    {
        var result = new List<PgPlStatement>();
        var tryStatements = tc.TryStatements.Statements.ToList();
        bool outermost = st.TryDepth == 0;
        bool beginsTransaction = Contains<BeginTransactionStatement>(tryStatements);
        bool commitAfter = false;
        if (outermost)
        {
            // BEGIN TRAN in the TRY: what came before it was committed in SQL Server (autocommit).
            if (beginsTransaction && st.Signature.Kind != BodyKind.Trigger)
                result.Add(new PgTransactionControl(true));
            if (tryStatements.LastOrDefault() is CommitTransactionStatement)
            {
                commitAfter = true;
                tryStatements.RemoveAt(tryStatements.Count - 1);
            }
        }

        if (!beginsTransaction && !st.TransactionBegun && KeepsEarlierWrites(tryStatements))
            st.C.Report(DiagnosticCodes.TryUndoesWrites, Severity.Warning,
                "This TRY changes data outside a transaction, then runs more statements: when a later one fails, SQL Server keeps the changes already made, and PostgreSQL's exception block undoes them all.");

        List<PgPlStatement> body;
        st.TryDepth++;
        try
        {
            body = List(tryStatements, st);
        }
        finally
        {
            st.TryDepth--;
        }

        var frame = new ErrorFrame(st.ErrorFrames.Count == 0 ? "" : $"_{st.ErrorFrames.Count + 1}",
            beginsTransaction || st.TransactionBegun || st.TryDepth > 0);
        var handler = new List<PgPlStatement>();
        st.ErrorFrames.Push(frame);
        try
        {
            handler.AddRange(Diagnostics(tc.CatchStatements, frame, st));
            handler.AddRange(List(tc.CatchStatements.Statements, st));
        }
        finally
        {
            st.ErrorFrames.Pop();
        }
        if (handler.Count == 0)
            handler.Add(PgNullStatement.Instance);
        result.Add(new PgBlock(body) { Handlers = [new PgExceptionHandler(["OTHERS"], handler)] });
        if (commitAfter)
            result.Add(new PgTransactionControl(true));
        return result;
    }

    /// <summary>GET STACKED DIAGNOSTICS for the ERROR_*() functions the CATCH block uses, then the T-SQL error number.</summary>
    private IEnumerable<PgPlStatement> Diagnostics(StatementList catchStatements, ErrorFrame frame, BodyState st)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finder = new Finder(f =>
        {
            if (f is FunctionCall { CallTarget: null } call)
                used.Add(call.FunctionName.Value);
            return false;
        });
        catchStatements.Accept(finder);

        var items = new List<PgDiagnosticsItem>();
        if (used.Contains("ERROR_MESSAGE"))
            items.Add(new(Helper(ErrorHelper("message", frame), "text", st).Name, "MESSAGE_TEXT"));
        if (used.Contains("ERROR_NUMBER"))
        {
            items.Add(new(Helper(ErrorHelper("sqlstate", frame), "text", st).Name, "RETURNED_SQLSTATE"));
            items.Add(new(Helper(ErrorHelper("detail", frame), "text", st).Name, "PG_EXCEPTION_DETAIL"));
        }
        if (items.Count == 0)
            yield break;
        yield return new PgGetDiagnostics(items, Stacked: true);
        if (!used.Contains("ERROR_NUMBER"))
            yield break;

        // THROW n, … writes its number in DETAIL as mssql_error=n; RAISERROR's message is 50000.
        var sqlState = new PgColumnRef(ErrorHelper("sqlstate", frame));
        var detail = new PgColumnRef(ErrorHelper("detail", frame));
        var number = new System.Text.StringBuilder("CASE");
        number.Append(" WHEN {0} = 'P0001' AND {1} LIKE 'mssql\\_error=%' THEN CAST(substr({1}, 13) AS integer)");
        foreach (var (state, n) in ErrorNumbers)
            number.Append($" WHEN {{0}} = '{state}' THEN {n}");
        number.Append(" ELSE 50000 END");
        yield return new PgAssign(Helper(ErrorHelper("number", frame), "integer", st), new PgTemplate(number.ToString(), [sqlState, detail]));
    }

    /// <summary>
    /// Whether a statement that changes data can be followed by another that fails: two or more
    /// such statements, or one that is not the TRY's last statement.
    /// </summary>
    private static bool KeepsEarlierWrites(IReadOnlyList<TSqlStatement> statements)
    {
        static bool Writes(TSqlFragment f) => f is TSqlStatement s && (IsDml(s) || s is ExecuteStatement or TruncateTableStatement);
        int writes = 0;
        var finder = new Finder(f =>
        {
            if (Writes(f))
                writes++;
            return false;
        });
        foreach (var s in statements)
            s.Accept(finder);
        if (writes >= 2)
            return true;
        for (int i = 0; i < statements.Count - 1; i++)
            if (Contains<TSqlStatement>([statements[i]], Writes))
                return true;
        return false;
    }

    private static bool Contains<T>(IEnumerable<TSqlStatement> statements, Func<TSqlFragment, bool> test) where T : TSqlFragment
    {
        var finder = new Finder(f => f is T && test(f));
        foreach (var s in statements)
            s.Accept(finder);
        return finder.Found;
    }

    private static bool Contains<T>(IEnumerable<TSqlStatement> statements) where T : TSqlFragment
    {
        var finder = new Finder(f => f is T);
        foreach (var s in statements)
            s.Accept(finder);
        return finder.Found;
    }

    /// <summary>ERROR_*() and XACT_STATE(): in a CATCH block from its helpers, elsewhere NULL (as in SQL Server outside CATCH).</summary>
    private PgExpr? ErrorFunction(string name, ExpressionContext c, BodyState st)
    {
        if (name == "XACT_STATE")
            return TransactionState(c, st, "XACT_STATE()", "-1");
        if (!st.ErrorFrames.TryPeek(out var frame))
        {
            c.Report(DiagnosticCodes.ErrorFunctionOutsideCatch, Severity.Warning,
                $"{name}() outside a CATCH block is NULL. SQL Server returns the error of the CATCH block that called this code (an error-logging procedure); PostgreSQL does not pass it on.");
            return PgLiteral.Null;
        }
        switch (name)
        {
            case "ERROR_MESSAGE":
                return new PgColumnRef(ErrorHelper("message", frame));
            case "ERROR_NUMBER":
                return new PgColumnRef(ErrorHelper("number", frame));
            case "ERROR_SEVERITY":
                return new PgLiteral("16");
            case "ERROR_STATE":
                c.Report(DiagnosticCodes.ErrorLineState, Severity.Info, "ERROR_STATE() is 1: PostgreSQL errors have no state number.");
                return new PgLiteral("1");
            case "ERROR_LINE":
                c.Report(DiagnosticCodes.ErrorLineState, Severity.Info, "ERROR_LINE() is NULL: PostgreSQL reports the line in the error's context, not as a number.");
                return new PgCast(PgLiteral.Null, "integer");
            case "ERROR_PROCEDURE":
                // SQL Server 2017 and later return the schema-qualified name.
                return st.Signature.Shape?.Name is { } procedure ? PgLiteral.String($"{procedure.Schema}.{procedure.Name}")
                    : st.Signature.RoutineName is { } routine ? PgLiteral.String(routine)
                    : new PgCast(PgLiteral.Null, "text");
            default:
                return null;
        }
    }

    /// <summary>
    /// XACT_STATE() and @@TRANCOUNT in a CATCH block: <paramref name="open"/> when a transaction
    /// may be open (a BEGIN TRAN in the TRY or before it), else 0. A ROLLBACK there then undoes
    /// the work since that BEGIN TRAN, as in SQL Server. Outside CATCH there is no translation.
    /// </summary>
    private PgExpr TransactionState(ExpressionContext c, BodyState st, string what, string open)
    {
        if (!st.ErrorFrames.TryPeek(out var frame))
        {
            if (what == "@@TRANCOUNT")
                throw new UntranslatableException(DiagnosticCodes.TranCount,
                    $"{what} outside a CATCH block has no PostgreSQL equivalent: a routine always runs in a transaction.");
            // Outside a handler the transaction is active and committable.
            c.Report(DiagnosticCodes.TransactionState, Severity.Info,
                $"{what} outside CATCH is 1: PL/pgSQL code always runs in an active transaction (SQL Server returns 0 outside a transaction, and -1 in code called from a CATCH block whose transaction is doomed).");
            return new PgLiteral("1");
        }
        c.Report(DiagnosticCodes.TransactionState, Severity.Info,
            frame.TransactionMayBeOpen
                ? $"{what} in CATCH is {open}: a BEGIN TRAN may have run, and a ROLLBACK here undoes the work since it (the TRY's own work is already undone)."
                : $"{what} in CATCH is 0: no BEGIN TRAN comes before it (the TRY's work is already undone).");
        return new PgLiteral(frame.TransactionMayBeOpen ? open : "0");
    }
}
