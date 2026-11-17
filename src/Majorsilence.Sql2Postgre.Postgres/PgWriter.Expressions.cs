using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <summary>Prints the PostgreSQL tree as SQL text: keywords upper case, names quoted where needed.</summary>
public static partial class PgWriter
{
    /// <summary>
    /// How tightly each construct binds, loosest first, following the operator precedence table in
    /// the PostgreSQL manual (section "Lexical Structure", "Operator Precedence"). A child that
    /// binds more loosely than its position needs is parenthesized; nothing else is.
    /// </summary>
    private enum Binding
    {
        Or = 1, And, Not, Is, Comparison, BetweenInLike, OtherOperator, Additive, Multiplicative,
        Exponent, AtTimeZone, Unary, Atom,
    }

    public static string Expression(PgExpr e) => e switch
    {
        PgLiteral l => l.Sql,
        PgKeyword k => k.Sql,
        PgColumnRef c => c.Qualifier is null ? NameMapper.Quote(c.Name) : $"{NameMapper.Quote(c.Qualifier)}.{NameMapper.Quote(c.Name)}",
        PgUnary { Operator: "NOT" } u => $"NOT {Child(u.Operand, Binding.Not, strict: false)}",
        PgUnary u => $"{u.Operator}{Child(u.Operand, Binding.Unary, strict: false)}",
        PgBinary b => Binary(b),
        PgIsNull n => $"{Child(n.Operand, Binding.Is, strict: true)} IS {(n.Negated ? "NOT " : "")}NULL",
        PgInList i => $"{Child(i.Operand, Binding.BetweenInLike, strict: true)} {(i.Negated ? "NOT " : "")}IN ({string.Join(", ", i.Values.Select(Expression))})",
        PgBetween b => $"{Child(b.Operand, Binding.BetweenInLike, strict: true)} {(b.Negated ? "NOT " : "")}BETWEEN "
                       + $"{Child(b.Low, Binding.BetweenInLike, strict: true)} AND {Child(b.High, Binding.BetweenInLike, strict: true)}",
        PgLike l => $"{Child(l.Operand, Binding.BetweenInLike, strict: true)} {(l.Negated ? "NOT " : "")}{(l.Similar ? "SIMILAR TO" : "LIKE")} {Child(l.Pattern, Binding.BetweenInLike, strict: true)}"
                    + (l.Escape is null ? "" : $" ESCAPE {Child(l.Escape, Binding.BetweenInLike, strict: true)}"),
        PgCase c => Case(c),
        PgCast c => $"CAST({Expression(c.Operand)} AS {c.Type})",
        PgFunctionCall f => Function(f),
        PgTemplate t => Template(t),
        PgDynamicSql d => DynamicSql(d),
        PgSubquery q => $"({Query(q.Query, inline: true)})",
        PgExists ex => $"{(ex.Negated ? "NOT " : "")}EXISTS ({Query(ex.Query, inline: true)})",
        PgInSubquery i => $"{Child(i.Operand, Binding.BetweenInLike, strict: true)} {(i.Negated ? "NOT " : "")}IN ({Query(i.Query, inline: true)})",
        PgQuantified q => $"{Child(q.Operand, Binding.Comparison, strict: true)} {q.Operator} {q.Quantifier} ({Query(q.Query, inline: true)})",
        PgAtTimeZone a => $"{Child(a.Operand, Binding.AtTimeZone, strict: true)} AT TIME ZONE {Child(a.Zone, Binding.AtTimeZone, strict: true)}",
        _ => throw new ArgumentException($"Cannot write {e.GetType().Name}.", nameof(e)),
    };

    private static string Binary(PgBinary b)
    {
        var binding = BindingOf(b);
        // AND, OR and || give the same answer however they group, so an equal-binding right-hand
        // side needs no parentheses; for everything else it does (a - (b - c)), and comparisons
        // do not chain at all.
        bool associative = b.Operator is "AND" or "OR" or "||" || (b.Right is PgBinary rb && rb.Operator == b.Operator && b.Operator is "+" or "*");
        bool leftStrict = binding == Binding.Comparison;
        bool rightStrict = binding == Binding.Comparison || !associative;
        return $"{Child(b.Left, binding, leftStrict)} {b.Operator} {Child(b.Right, binding, rightStrict)}";
    }

    /// <summary>Dynamic SQL as a string literal, or format() when markers stand for run-time names and values.</summary>
    private static string DynamicSql(PgDynamicSql d)
    {
        string sql = string.Join("\n", d.Statements.Select(s => Write(s, 18).TrimEnd('\n').TrimEnd(';')));
        if (d.Arguments.Count == 0)
            return PgLiteral.String(sql).Sql;
        sql = sql.Replace("%", "%%");
        // Positional (%2$L): the translation may move the pieces (TOP becomes LIMIT at the end).
        var numbered = d.Arguments.Select((a, i) => (a.Marker, Spec: $"%{i + 1}${a.Spec}"));
        foreach (var (marker, spec) in numbered.OrderByDescending(a => a.Marker.Length).ThenByDescending(a => a.Marker, StringComparer.Ordinal))
            sql = sql.Replace(marker, spec);
        return $"format({PgLiteral.String(sql).Sql}, {string.Join(", ", d.Arguments.Select(a => Expression(a.Value)))})";
    }

    private static string Template(PgTemplate t)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < t.Template.Length; i++)
        {
            if (t.Template[i] == '{' && i + 2 < t.Template.Length && char.IsDigit(t.Template[i + 1]))
            {
                int close = t.Template.IndexOf('}', i);
                int n = int.Parse(t.Template.AsSpan(i + 1, close - i - 1), System.Globalization.CultureInfo.InvariantCulture);
                var arg = t.Arguments[n];
                string text = Expression(arg);
                sb.Append(BindingOf(arg) == Binding.Atom ? text : $"({text})");
                i = close;
            }
            else
                sb.Append(t.Template[i]);
        }
        return sb.ToString();
    }

    private static string Function(PgFunctionCall f)
    {
        string args = f.Star ? "*" : string.Join(", ", f.Arguments.Select(Expression));
        if (f.Distinct)
            args = "DISTINCT " + args;
        if (f.OrderBy.Count > 0)
            args += " ORDER BY " + SortKeys(f.OrderBy);
        string call = $"{f.Name}({args})";
        if (f.Filter is not null)
            call += $" FILTER (WHERE {Expression(f.Filter)})";
        return f.Over is null ? call : $"{call} OVER ({Window(f.Over)})";
    }

    public static string SortKeys(IEnumerable<PgSortKey> keys) => string.Join(", ", keys.Select(k =>
        Expression(k.Expression) + (k.Descending ? " DESC" : "") + (k.NullsFirst switch { true => " NULLS FIRST", false => " NULLS LAST", null => "" })));

    private static string Window(PgWindow w)
    {
        var parts = new List<string>();
        if (w.PartitionBy.Count > 0)
            parts.Add("PARTITION BY " + string.Join(", ", w.PartitionBy.Select(Expression)));
        if (w.OrderBy.Count > 0)
            parts.Add("ORDER BY " + SortKeys(w.OrderBy));
        if (w.Frame is not null)
            parts.Add(w.Frame);
        return string.Join(' ', parts);
    }

    private static string Case(PgCase c)
    {
        var parts = new List<string> { "CASE" };
        if (c.Operand is not null)
            parts.Add(Expression(c.Operand));
        foreach (var w in c.Whens)
            parts.Add($"WHEN {Expression(w.Condition)} THEN {Expression(w.Result)}");
        if (c.Else is not null)
            parts.Add($"ELSE {Expression(c.Else)}");
        parts.Add("END");
        return string.Join(' ', parts);
    }

    /// <param name="strict">The child must bind more tightly than <paramref name="position"/>, not merely as tightly.</param>
    private static string Child(PgExpr child, Binding position, bool strict)
    {
        var binding = BindingOf(child);
        bool parenthesize = strict ? binding <= position : binding < position;
        string text = Expression(child);
        return parenthesize ? $"({text})" : text;
    }

    private static Binding BindingOf(PgExpr e) => e switch
    {
        PgBinary { Operator: "OR" } => Binding.Or,
        PgBinary { Operator: "AND" } => Binding.And,
        PgUnary { Operator: "NOT" } => Binding.Not,
        PgIsNull => Binding.Is,
        PgBinary { Operator: "=" or "<>" or "<" or ">" or "<=" or ">=" } => Binding.Comparison,
        PgInList or PgBetween or PgLike or PgInSubquery => Binding.BetweenInLike,
        PgQuantified => Binding.Comparison,
        PgExists { Negated: true } => Binding.Not,
        PgBinary { Operator: "+" or "-" } => Binding.Additive,
        PgBinary { Operator: "*" or "/" or "%" } => Binding.Multiplicative,
        PgBinary { Operator: "^" } => Binding.Exponent,
        PgBinary => Binding.OtherOperator,
        PgAtTimeZone => Binding.AtTimeZone,
        PgUnary => Binding.Unary,
        // A negative literal binds like unary minus.
        PgLiteral { Sql: ['-', ..] } => Binding.Unary,
        _ => Binding.Atom,
    };
}
