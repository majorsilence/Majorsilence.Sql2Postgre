using System.Text;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <summary>Queries and DML: one clause per line at the top level, subqueries written on one line.</summary>
public static partial class PgWriter
{
    /// <param name="inline">Write on one line (a subquery inside an expression or FROM item).</param>
    public static string Query(PgQuery q, bool inline = false)
    {
        string nl = inline ? " " : "\n";
        return q switch
        {
            PgSelect s => Select(s, nl),
            PgSetOperation o => SetOperation(o, nl),
            PgModifyingQuery m => DmlOrQuery(m.Statement).TrimEnd('\n').TrimEnd(';'),
            PgValues v => "VALUES " + string.Join(", ", v.Rows.Select(r => $"({string.Join(", ", r.Select(Expression))})")),
            _ => throw new ArgumentException($"Cannot write {q.GetType().Name}.", nameof(q)),
        };
    }

    private static string With(IReadOnlyList<PgCte> ctes, bool recursive, string nl) =>
        ctes.Count == 0 ? "" :
            "WITH " + (recursive ? "RECURSIVE " : "") + string.Join(", ", ctes.Select(c =>
                Q(c.Name) + (c.Columns.Count > 0 ? $" ({Columns(c.Columns)})" : "") + $" AS ({Query(c.Query, inline: true)})")) + nl;

    /// <param name="into">PL/pgSQL's <c>INTO targets</c>, written after the select list.</param>
    private static string Select(PgSelect s, string nl, string? into = null)
    {
        var sb = new StringBuilder(With(s.With, s.Recursive, nl));
        sb.Append("SELECT ");
        if (s.Distinct)
            sb.Append("DISTINCT ");
        sb.Append(string.Join(", ", s.Targets.Select(Target)));
        if (into is not null)
            sb.Append(nl).Append(into);
        if (s.From.Count > 0)
            sb.Append(nl).Append("FROM ").Append(string.Join(", ", s.From.Select(FromItem)));
        if (s.Where is not null)
            sb.Append(nl).Append("WHERE ").Append(Expression(s.Where));
        if (s.GroupBy.Count > 0)
            sb.Append(nl).Append("GROUP BY ").Append(string.Join(", ", s.GroupBy.Select(GroupItem)));
        if (s.Having is not null)
            sb.Append(nl).Append("HAVING ").Append(Expression(s.Having));
        AppendTail(sb, s.OrderBy, s.Limit, s.Offset, s.WithTies, nl);
        if (s.ForUpdate)
            sb.Append(nl).Append("FOR UPDATE");
        return sb.ToString();
    }

    private static string SetOperation(PgSetOperation o, string nl)
    {
        var sb = new StringBuilder(With(o.With, o.Recursive, nl));
        sb.Append(SetSide(o.Left, nl)).Append(nl).Append(o.Operator).Append(nl).Append(SetSide(o.Right, nl));
        AppendTail(sb, o.OrderBy, o.Limit, o.Offset, false, nl);
        return sb.ToString();
    }

    /// <summary>A side of a set operation needs parentheses when it has its own ORDER BY or LIMIT.</summary>
    private static string SetSide(PgQuery q, string nl) =>
        q is PgSelect { OrderBy.Count: > 0 } or PgSelect { Limit: not null } or PgSelect { Offset: not null } or PgSelect { With.Count: > 0 }
            ? $"({Query(q, inline: true)})"
            : Query(q, nl == " ");

    private static void AppendTail(StringBuilder sb, IReadOnlyList<PgSortKey> orderBy, PgExpr? limit, PgExpr? offset, bool withTies, string nl)
    {
        if (orderBy.Count > 0)
            sb.Append(nl).Append("ORDER BY ").Append(SortKeys(orderBy));
        if (offset is not null)
            sb.Append(nl).Append("OFFSET ").Append(Expression(offset)).Append(withTies ? " ROWS" : "");
        if (limit is not null)
            sb.Append(nl).Append(withTies ? $"FETCH FIRST {Child(limit, Binding.Atom, strict: false)} ROWS WITH TIES" : $"LIMIT {Expression(limit)}");
    }

    private static string Target(PgTarget t) =>
        t.Star ? (t.StarQualifier is null ? "*" : $"{Q(t.StarQualifier)}.*")
        : t.Alias is null ? Expression(t.Expression!) : $"{Expression(t.Expression!)} AS {Q(t.Alias)}";

    private static string GroupItem(PgGroupItem g) => g switch
    {
        PgGroupExpr e => Expression(e.Expression),
        PgGroupSets { Kind: "" } s => string.Join(", ", s.Sets.Select(set => $"({string.Join(", ", set.Select(Expression))})")),
        PgGroupSets s => $"{s.Kind} ({string.Join(", ", s.Sets.Select(set => set.Count == 1 && s.Kind != "GROUPING SETS" ? Expression(set[0]) : $"({string.Join(", ", set.Select(Expression))})"))})",
        _ => throw new ArgumentException(g.GetType().Name),
    };

    public static string FromItem(PgFromItem f) => f switch
    {
        PgTableRef t => t.Name.Schema.Length == 0 ? Q(t.Name.Name) + AliasClause(t.Alias) : t.Name.Qualified + AliasClause(t.Alias),
        PgSubqueryRef s => $"{(s.Lateral ? "LATERAL " : "")}({Query(s.Query, inline: true)}) AS {Q(s.Alias)}" + (s.Columns.Count > 0 ? $" ({Columns(s.Columns)})" : ""),
        PgFunctionRef fn => $"{(fn.Lateral ? "LATERAL " : "")}{fn.Name}({string.Join(", ", fn.Arguments.Select(Expression))})"
                            + AliasClause(fn.Alias) + (fn.Columns.Count > 0 ? $" ({Columns(fn.Columns)})" : ""),
        PgJoin j => $"{FromItem(j.Left)} {j.Kind} {JoinRight(j.Right)}" + (j.On is null ? "" : $" ON {Expression(j.On)}"),
        _ => throw new ArgumentException(f.GetType().Name),
    };

    /// <summary>A join's right side is parenthesized when it is itself a join.</summary>
    private static string JoinRight(PgFromItem right) => right is PgJoin ? $"({FromItem(right)})" : FromItem(right);

    private static string AliasClause(string? alias) => alias is null ? "" : $" AS {Q(alias)}";

    private static string Returning(IReadOnlyList<PgTarget> returning) =>
        returning.Count == 0 ? "" : "\nRETURNING " + string.Join(", ", returning.Select(Target));

    private static string Assignments(IReadOnlyList<PgAssignment> set) =>
        string.Join(", ", set.Select(a => $"{Q(a.Column)} = {Expression(a.Value)}"));

    private static string Insert(PgInsert i) =>
        With(i.With, false, "\n") + $"INSERT INTO {i.Table.Qualified}"
        + (i.Columns.Count > 0 ? $" ({Columns(i.Columns)})" : "")
        + (i.Source is null ? " DEFAULT VALUES" : "\n" + Query(i.Source))
        + Returning(i.Returning) + ";\n";

    private static string Update(PgUpdate u) =>
        With(u.With, false, "\n") + $"UPDATE {u.Table.Qualified}{AliasClause(u.Alias)}\nSET {Assignments(u.Set)}"
        + (u.From.Count > 0 ? "\nFROM " + string.Join(", ", u.From.Select(FromItem)) : "")
        + (u.Where is null ? "" : "\nWHERE " + Expression(u.Where))
        + Returning(u.Returning) + ";\n";

    private static string Delete(PgDelete d) =>
        With(d.With, false, "\n") + $"DELETE FROM {d.Table.Qualified}{AliasClause(d.Alias)}"
        + (d.Using.Count > 0 ? "\nUSING " + string.Join(", ", d.Using.Select(FromItem)) : "")
        + (d.Where is null ? "" : "\nWHERE " + Expression(d.Where))
        + Returning(d.Returning) + ";\n";

    private static string Merge(PgMerge m)
    {
        var sb = new StringBuilder(With(m.With, false, "\n"));
        sb.Append($"MERGE INTO {m.Table.Qualified}{AliasClause(m.Alias)}\nUSING {FromItem(m.Source)}\nON {Expression(m.On)}");
        foreach (var w in m.Whens)
        {
            sb.Append($"\nWHEN {w.Condition}");
            if (w.And is not null)
                sb.Append($" AND {Expression(w.And)}");
            sb.Append(" THEN ").Append(w.Action switch
            {
                PgMergeUpdate u => $"UPDATE SET {Assignments(u.Set)}",
                PgMergeDelete => "DELETE",
                PgMergeInsert { Values: null } ins => "INSERT" + (ins.Columns.Count > 0 ? $" ({Columns(ins.Columns)})" : "") + " DEFAULT VALUES",
                PgMergeInsert ins => "INSERT" + (ins.Columns.Count > 0 ? $" ({Columns(ins.Columns)})" : "") + $" VALUES ({string.Join(", ", ins.Values!.Select(Expression))})",
                _ => throw new ArgumentException(w.Action.GetType().Name),
            });
        }
        return sb.Append(Returning(m.Returning)).Append(";\n").ToString();
    }

    private static string DmlOrQuery(PgStatement statement) => statement switch
    {
        PgQueryStatement q => Query(q.Query) + ";\n",
        PgCreateView v => $"CREATE {(v.Materialized ? "MATERIALIZED " : "")}VIEW {v.Name.Qualified}"
                          + (v.Columns.Count > 0 ? $" ({Columns(v.Columns)})" : "") + $" AS\n{Query(v.Query)}"
                          + (v.CheckOption ? "\nWITH CHECK OPTION" : "") + ";\n",
        PgCreateTableAs c => $"CREATE {(c.Temporary ? "TEMP " : "")}TABLE {(c.Name.Schema.Length == 0 ? Q(c.Name.Name) : c.Name.Qualified)} AS\n{Query(c.Query)};\n",
        PgInsert i => Insert(i),
        PgUpdate u => Update(u),
        PgDelete d => Delete(d),
        PgMerge m => Merge(m),
        _ => throw new ArgumentException($"Cannot write {statement.GetType().Name}.", nameof(statement)),
    };
}
