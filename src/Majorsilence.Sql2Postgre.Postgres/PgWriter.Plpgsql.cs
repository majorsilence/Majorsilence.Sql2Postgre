using System.Text;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <summary>
/// Routines and PL/pgSQL bodies: one statement per line (multi-line SQL statements keep the
/// query layout), four-space indent per nesting level, bodies in dollar quotes.
/// </summary>
public static partial class PgWriter
{
    private static string Routine(PgCreateRoutine r, int targetVersion)
    {
        var sb = new StringBuilder($"CREATE {(r.OrReplace ? "OR REPLACE " : "")}{(r.Procedure ? "PROCEDURE" : "FUNCTION")} {r.Name.Qualified}(");
        sb.Append(string.Join(", ", r.Parameters.Select(Parameter))).Append(")\n");
        if (!r.Procedure)
        {
            if (r.ReturnsTable.Count > 0)
                sb.Append("RETURNS TABLE (").Append(string.Join(", ", r.ReturnsTable.Select(f => $"{Q(f.Name)} {f.Type}"))).Append(")\n");
            else
                sb.Append("RETURNS ").Append(r.Returns ?? throw new ArgumentException($"Function {r.Name} has no return type.", nameof(r))).Append('\n');
        }
        sb.Append("LANGUAGE ").Append(r.Language).Append('\n');
        if (r.Volatility is not null && !r.Procedure)
            sb.Append(r.Volatility).Append('\n');

        switch (r.Body)
        {
            case PgSqlReturnBody ret when !r.Procedure:
                return sb.Append("RETURN ").Append(Expression(ret.Value)).Append(";\n").ToString();
            case PgSqlReturnBody:
                throw new ArgumentException($"Procedure {r.Name} cannot have a RETURN body.", nameof(r));
            case PgSqlBody sql:
                return sb.Append(DollarQuoted(string.Concat(sql.Statements.Select(s => Write(s, targetVersion))))).Append(";\n").ToString();
            case PgPlBody pl:
                var body = string.Concat(pl.Directives.Select(d => d + "\n")) + PlStatement(pl.Block, targetVersion);
                return sb.Append(DollarQuoted(body)).Append(";\n").ToString();
            default:
                throw new ArgumentException(r.Body.GetType().Name, nameof(r));
        }
    }

    private static string Parameter(PgParameter p) =>
        (p.Mode is "IN" or "" ? "" : p.Mode + " ") + $"{Q(p.Name)} {p.Type}" + (p.Default is null ? "" : $" DEFAULT {Expression(p.Default)}");

    /// <summary>
    /// <c>AS $$ … $$</c>, or <c>$body$</c> (then <c>$body1$</c>, …) when the body itself contains the
    /// tag; the body (including comments and string literals) must not contain its own end tag.
    /// </summary>
    internal static string DollarQuoted(string body)
    {
        string tag = "$$";
        for (int n = 0; body.Contains(tag, StringComparison.Ordinal); n++)
            tag = n == 0 ? "$body$" : $"$body{n}$";
        return $"AS {tag}\n{body}{tag}";
    }

    /// <summary>Writes one PL/pgSQL statement, unindented, ending in a new line.</summary>
    public static string PlStatement(PgPlStatement s, int targetVersion) => s switch
    {
        PgBlock b => Block(b, targetVersion),
        PgAssign a => $"{Expression(a.Target)} := {Hang(Expression(a.Value))};\n",
        PgIf i => If(i, targetVersion),
        PgWhile w => LabelLine(w.Label) + $"WHILE {Hang(Expression(w.Condition))} LOOP\n" + Nested(w.Statements, targetVersion) + EndLoop(w.Label),
        PgLoop l => LabelLine(l.Label) + "LOOP\n" + Nested(l.Statements, targetVersion) + EndLoop(l.Label),
        PgExit e => "EXIT" + LabelRef(e.Label) + (e.When is null ? "" : $" WHEN {Expression(e.When)}") + ";\n",
        PgContinue c => "CONTINUE" + LabelRef(c.Label) + (c.When is null ? "" : $" WHEN {Expression(c.When)}") + ";\n",
        PgForQuery f => LabelLine(f.Label) + $"FOR {Q(f.Target)} IN {DynamicOrQuery(f.Query, f.Execute, f.Using)}\nLOOP\n"
                        + Nested(f.Statements, targetVersion) + EndLoop(f.Label),
        PgForRange f => LabelLine(f.Label) + $"FOR {Q(f.Variable)} IN {(f.Reverse ? "REVERSE " : "")}{Expression(f.Low)} .. {Expression(f.High)}"
                        + (f.Step is null ? "" : $" BY {Expression(f.Step)}") + " LOOP\n" + Nested(f.Statements, targetVersion) + EndLoop(f.Label),
        PgReturn r => r.Value is null ? "RETURN;\n" : $"RETURN {Hang(Expression(r.Value))};\n",
        PgReturnQuery r => $"RETURN QUERY {DynamicOrQuery(r.Query, r.Execute, r.Using)};\n",
        PgReturnNext r => r.Value is null ? "RETURN NEXT;\n" : $"RETURN NEXT {Expression(r.Value)};\n",
        PgRaise r => Raise(r),
        PgAssert a => $"ASSERT {Expression(a.Condition)}" + (a.Message is null ? "" : $", {Expression(a.Message)}") + ";\n",
        PgPerform p => Perform(p.Query),
        PgExecuteDynamic e => $"EXECUTE {Hang(Expression(e.Command))}" + Into(e.Into, e.Strict, " ") + UsingClause(e.Using) + ";\n",
        PgGetDiagnostics g => $"GET {(g.Stacked ? "STACKED " : "")}DIAGNOSTICS "
                              + string.Join(", ", g.Items.Select(i => $"{Q(i.Target)} = {i.Item}")) + ";\n",
        PgOpenCursor o => OpenCursor(o),
        PgFetch f => Fetch(f),
        PgClose c => $"CLOSE {Q(c.Cursor)};\n",
        PgTransactionControl t => (t.Commit ? "COMMIT" : "ROLLBACK") + (t.AndChain ? " AND CHAIN" : "") + ";\n",
        PgCall c => $"CALL {c.Procedure.Qualified}("
                     + string.Join(", ", c.Arguments.Select(a => a.Name is null ? Expression(a.Value) : $"{Q(a.Name)} => {Expression(a.Value)}")) + ");\n",
        PgSqlStatement q => SqlStatement(q, targetVersion),
        PgNullStatement => "NULL;\n",
        _ => throw new ArgumentException($"Cannot write {s.GetType().Name}.", nameof(s)),
    };

    private static string Block(PgBlock b, int targetVersion)
    {
        var sb = new StringBuilder(LabelLine(b.Label));
        if (b.Declarations.Count > 0)
        {
            sb.Append("DECLARE\n");
            foreach (var d in b.Declarations)
                sb.Append(IndentLines(Declaration(d), Indent));
        }
        sb.Append("BEGIN\n").Append(Nested(b.Statements, targetVersion));
        if (b.Handlers.Count > 0)
        {
            sb.Append("EXCEPTION\n");
            foreach (var h in b.Handlers)
                sb.Append(Indent).Append("WHEN ").Append(string.Join(" OR ", h.Conditions)).Append(" THEN\n")
                    .Append(IndentLines(Nested(h.Statements, targetVersion), Indent));
        }
        return sb.Append("END").Append(LabelRef(b.Label)).Append(";\n").ToString();
    }

    private static string Declaration(PgDeclaration d) => d switch
    {
        PgDeclare v => $"{Q(v.Name)} {(v.Constant ? "CONSTANT " : "")}{v.Type}{(v.NotNull ? " NOT NULL" : "")}"
                       + (v.Default is null ? "" : $" := {Hang(Expression(v.Default))}") + ";\n",
        PgDeclareCursor c => $"{Q(c.Name)} {c.Scroll switch { true => "SCROLL ", false => "NO SCROLL ", null => "" }}CURSOR"
                             + (c.Arguments.Count > 0 ? $" ({string.Join(", ", c.Arguments.Select(a => $"{Q(a.Name)} {a.Type}"))})" : "")
                             + $" FOR {Hang(Query(c.Query))};\n",
        _ => throw new ArgumentException(d.GetType().Name, nameof(d)),
    };

    private static string If(PgIf i, int targetVersion)
    {
        if (i.Branches.Count == 0)
            throw new ArgumentException("IF needs at least one branch.", nameof(i));
        var sb = new StringBuilder();
        for (int n = 0; n < i.Branches.Count; n++)
            sb.Append(n == 0 ? "IF " : "ELSIF ").Append(Hang(Expression(i.Branches[n].Condition))).Append(" THEN\n")
                .Append(Nested(i.Branches[n].Statements, targetVersion));
        if (i.Else is not null)
            sb.Append("ELSE\n").Append(Nested(i.Else, targetVersion));
        return sb.Append("END IF;\n").ToString();
    }

    private static string Raise(PgRaise r)
    {
        if (r.Level is null)
            return r.Format is null && r.Arguments.Count == 0 && r.Using.Count == 0
                ? "RAISE;\n"
                : throw new ArgumentException("A re-throwing RAISE takes no format, arguments or options.", nameof(r));
        int placeholders = r.Format is null ? 0 : CountPlaceholders(r.Format);
        if (placeholders != r.Arguments.Count)
            throw new ArgumentException($"RAISE format has {placeholders} placeholders for {r.Arguments.Count} arguments.", nameof(r));
        if (r.Format is null && r.Using.Count == 0)
            throw new ArgumentException("RAISE needs a format or a USING option.", nameof(r));

        var sb = new StringBuilder("RAISE ").Append(r.Level);
        if (r.Format is not null)
            sb.Append(' ').Append(Str(r.Format)).Append(string.Concat(r.Arguments.Select(a => ", " + Expression(a))));
        if (r.Using.Count > 0)
            sb.Append(" USING ").Append(string.Join(", ", r.Using.Select(o => $"{o.Name} = {Expression(o.Value)}")));
        return sb.Append(";\n").ToString();
    }

    /// <summary>The <c>%</c> placeholders in a RAISE format; <c>%%</c> is a literal percent sign.</summary>
    private static int CountPlaceholders(string format)
    {
        int count = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
                continue;
            if (i + 1 < format.Length && format[i + 1] == '%')
                i++;
            else
                count++;
        }
        return count;
    }

    /// <summary>
    /// PERFORM replaces SELECT's keyword; a query that does not start with SELECT (a CTE, a set
    /// operation, VALUES) is run as a subquery.
    /// </summary>
    private static string Perform(PgQuery q)
    {
        if (q is PgSelect { With.Count: 0 } s)
        {
            string text = Query(s);
            return "PERFORM " + Hang(text["SELECT ".Length..]) + ";\n";
        }
        return $"PERFORM FROM ({Query(q, inline: true)}) AS q;\n";
    }

    private static string OpenCursor(PgOpenCursor o)
    {
        var sb = new StringBuilder($"OPEN {Q(o.Cursor)}");
        if (o.Query is null && o.Execute is null)
        {
            if (o.Arguments.Count > 0)
                sb.Append('(').Append(string.Join(", ", o.Arguments.Select(Expression))).Append(')');
            return sb.Append(";\n").ToString();
        }
        sb.Append(o.Scroll switch { true => " SCROLL", false => " NO SCROLL", null => "" });
        return sb.Append(" FOR ").Append(DynamicOrQuery(o.Query, o.Execute, o.Using)).Append(";\n").ToString();
    }

    private static string Fetch(PgFetch f)
    {
        if (!f.Move && f.Into.Count == 0)
            throw new ArgumentException("FETCH needs INTO targets.", nameof(f));
        string direction = f.Direction + (f.Count is null ? "" : " " + Expression(f.Count));
        return f.Move
            ? $"MOVE {direction} FROM {Q(f.Cursor)};\n"
            : $"FETCH {direction} FROM {Q(f.Cursor)}{Into(f.Into, false, " ")};\n";
    }

    private static string SqlStatement(PgSqlStatement q, int targetVersion)
    {
        if (q.Into.Count == 0)
            return Write(q.Statement, targetVersion);
        if (q.Statement is PgQueryStatement { Query: PgSelect s })
            return Select(s, "\n", Into(q.Into, q.Strict, "\n").TrimStart('\n')) + ";\n";
        if (q.Statement is not (PgQueryStatement or PgInsert or PgUpdate or PgDelete or PgMerge))
            throw new ArgumentException($"{q.Statement.GetType().Name} cannot take INTO.", nameof(q));
        string text = Write(q.Statement, targetVersion);
        return text[..^2] + Into(q.Into, q.Strict, "\n") + ";\n";
    }

    private static string Into(IReadOnlyList<PgExpr> into, bool strict, string separator) =>
        into.Count == 0 ? "" : $"{separator}INTO {(strict ? "STRICT " : "")}{string.Join(", ", into.Select(Expression))}";

    private static string UsingClause(IReadOnlyList<PgExpr> args) =>
        args.Count == 0 ? "" : " USING " + string.Join(", ", args.Select(Expression));

    private static string DynamicOrQuery(PgQuery? query, PgExpr? execute, IReadOnlyList<PgExpr> args) =>
        execute is not null ? $"EXECUTE {Hang(Expression(execute))}{UsingClause(args)}"
        : query is not null ? Hang(Query(query))
        : throw new ArgumentException("Needs a query or an EXECUTE command.");

    private static string Nested(IReadOnlyList<PgPlStatement> statements, int targetVersion) =>
        IndentLines(string.Concat(statements.Select(st => PlStatement(st, targetVersion))), Indent);

    private static string LabelLine(string? label) => label is null ? "" : $"<<{Q(label)}>>\n";

    private static string LabelRef(string? label) => label is null ? "" : " " + Q(label);

    private static string EndLoop(string? label) => $"END LOOP{LabelRef(label)};\n";

    /// <summary>Indents the continuation lines of a multi-line fragment so it hangs under its statement.</summary>
    private static string Hang(string text) => text.Contains('\n') ? IndentLines(text, Indent, skipFirst: true) : text;

    /// <summary>
    /// Prefixes each non-empty line with <paramref name="indent"/>, except lines that begin inside a
    /// string literal or quoted identifier, whose content a prefix would change.
    /// </summary>
    public static string IndentLines(string text, string indent, bool skipFirst = false)
    {
        var sb = new StringBuilder(text.Length + 64);
        var state = LexState.Normal;
        string? dollarTag = null;
        bool atLineStart = true;
        bool first = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (atLineStart)
            {
                atLineStart = false;
                bool inLiteral = state is LexState.Single or LexState.EscapeSingle or LexState.Double or LexState.Dollar;
                if (c != '\n' && !inLiteral && !(first && skipFirst))
                    sb.Append(indent);
                first = false;
            }
            sb.Append(c);
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            switch (state)
            {
                case LexState.Normal:
                    if (c == '\'')
                        state = i > 0 && text[i - 1] is 'E' or 'e' && (i < 2 || !IsIdentifierChar(text[i - 2])) ? LexState.EscapeSingle : LexState.Single;
                    else if (c == '"')
                        state = LexState.Double;
                    else if (c == '-' && next == '-')
                        state = LexState.LineComment;
                    else if (c == '/' && next == '*')
                        state = LexState.BlockComment;
                    else if (c == '$' && (i == 0 || !IsIdentifierChar(text[i - 1])) && DollarTagAt(text, i) is { } tag)
                    {
                        dollarTag = tag;
                        sb.Append(tag, 1, tag.Length - 1);
                        i += tag.Length - 1;
                        state = LexState.Dollar;
                    }
                    break;
                case LexState.Single when c == '\'':
                case LexState.Double when c == '"':
                    state = LexState.Normal;
                    break;
                case LexState.EscapeSingle when c == '\\' && next != '\0':
                    sb.Append(next);
                    i++;
                    break;
                case LexState.EscapeSingle when c == '\'':
                    state = LexState.Normal;
                    break;
                case LexState.LineComment when c == '\n':
                    state = LexState.Normal;
                    break;
                case LexState.BlockComment when c == '*' && next == '/':
                    sb.Append(next);
                    i++;
                    state = LexState.Normal;
                    break;
                case LexState.Dollar when c == '$' && string.CompareOrdinal(text, i, dollarTag, 0, dollarTag!.Length) == 0:
                    sb.Append(dollarTag, 1, dollarTag.Length - 1);
                    i += dollarTag.Length - 1;
                    state = LexState.Normal;
                    break;
            }
            atLineStart = text[i] == '\n';
        }
        return sb.ToString();
    }

    private enum LexState { Normal, Single, EscapeSingle, Double, LineComment, BlockComment, Dollar }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The dollar-quote tag starting at <paramref name="start"/> (<c>$$</c> or <c>$tag$</c>), or null.</summary>
    private static string? DollarTagAt(string text, int start)
    {
        int i = start + 1;
        if (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;
        return i < text.Length && text[i] == '$' ? text[start..(i + 1)] : null;
    }
}
