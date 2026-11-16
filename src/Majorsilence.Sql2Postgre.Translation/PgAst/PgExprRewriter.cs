namespace Majorsilence.Sql2Postgre.Translation.PgAst;

/// <summary>Walks and rebuilds expression trees. Subqueries are leaves: neither entered nor rebuilt.</summary>
public static class PgExprRewriter
{
    /// <summary>
    /// The expression with every node for which <paramref name="replace"/> returns a value
    /// replaced by it (the replacement is not entered); other nodes are rebuilt from their
    /// rewritten children.
    /// </summary>
    public static PgExpr Rewrite(PgExpr e, Func<PgExpr, PgExpr?> replace)
    {
        if (replace(e) is { } replaced)
            return replaced;
        PgExpr R(PgExpr x) => Rewrite(x, replace);
        IReadOnlyList<PgExpr> All(IReadOnlyList<PgExpr> xs) => xs.Select(R).ToList();
        IReadOnlyList<PgSortKey> Keys(IReadOnlyList<PgSortKey> keys) => keys.Select(k => k with { Expression = R(k.Expression) }).ToList();
        return e switch
        {
            PgUnary u => u with { Operand = R(u.Operand) },
            PgBinary b => b with { Left = R(b.Left), Right = R(b.Right) },
            PgIsNull n => n with { Operand = R(n.Operand) },
            PgInList i => i with { Operand = R(i.Operand), Values = All(i.Values) },
            PgBetween b => b with { Operand = R(b.Operand), Low = R(b.Low), High = R(b.High) },
            PgLike l => l with { Operand = R(l.Operand), Pattern = R(l.Pattern), Escape = l.Escape is null ? null : R(l.Escape) },
            PgCase c => c with
            {
                Operand = c.Operand is null ? null : R(c.Operand),
                Whens = c.Whens.Select(w => new PgWhen(R(w.Condition), R(w.Result))).ToList(),
                Else = c.Else is null ? null : R(c.Else),
            },
            PgCast c => c with { Operand = R(c.Operand) },
            PgFunctionCall f => f with
            {
                Arguments = All(f.Arguments),
                OrderBy = Keys(f.OrderBy),
                Filter = f.Filter is null ? null : R(f.Filter),
                Over = f.Over is null ? null : f.Over with { PartitionBy = All(f.Over.PartitionBy), OrderBy = Keys(f.Over.OrderBy) },
            },
            PgAtTimeZone a => a with { Operand = R(a.Operand), Zone = R(a.Zone) },
            PgTemplate t => t with { Arguments = All(t.Arguments) },
            PgInSubquery s => s with { Operand = R(s.Operand) },
            PgQuantified q => q with { Operand = R(q.Operand) },
            _ => e,
        };
    }

    /// <summary>Whether any node of the expression (subqueries included as nodes, not entered) satisfies the test.</summary>
    public static bool Any(PgExpr e, Func<PgExpr, bool> test)
    {
        bool found = false;
        Rewrite(e, x =>
        {
            if (!found && test(x))
                found = true;
            return found ? x : null;
        });
        return found;
    }
}
