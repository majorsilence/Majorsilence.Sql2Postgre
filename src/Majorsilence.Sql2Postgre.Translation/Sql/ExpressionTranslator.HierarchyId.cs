using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// hierarchyid is kept as its text form (<c>/1/2/</c>, S2P1105). Its methods are mssql_compat
/// helpers over that text; ToString, Parse and a cast are the text itself, GetRoot is <c>/</c>.
/// Ordering (ORDER BY, <c>&lt;</c>, <c>&gt;</c>) uses <c>hierarchyid_key</c>, which sorts depth-first as
/// SQL Server does; the text alone would put <c>/10/</c> before <c>/2/</c>.
/// </summary>
public sealed partial class ExpressionTranslator
{
    private PgExpr HierarchyIdMethod(FunctionCall f, HierarchyIdCall call, ExpressionContext c)
    {
        var args = f.Parameters.Select(p => Scalar(p, c)).ToList();
        void Arity(int n)
        {
            if (args.Count != n)
                throw Untranslatable(f, $"{f.FunctionName.Value} with {args.Count} argument(s)");
        }
        if (call.Target is null)
        {
            if (call.Method == "getroot")
            {
                Arity(0);
                return PgLiteral.String("/");
            }
            Arity(1);
            return Text(f.Parameters[0], c);
        }
        var target = Scalar(call.Target, c);
        switch (call.Method)
        {
            case "tostring":
                Arity(0);
                return target;
            case "getlevel":
                Arity(0);
                return Compat("hierarchyid_getlevel", [target], c, f);
            case "getancestor":
                Arity(1);
                return Compat("hierarchyid_getancestor", [target, args[0]], c, f);
            case "isdescendantof":
                Arity(1);
                return Compat("hierarchyid_isdescendantof", [target, args[0]], c, f);
            case "getdescendant":
                Arity(2);
                return Compat("hierarchyid_getdescendant", [target, args[0], args[1]], c, f);
            case "getreparentedvalue":
                Arity(2);
                return Compat("hierarchyid_getreparentedvalue", [target, args[0], args[1]], c, f);
            default:
                throw MethodCall(f);
        }
    }

    /// <summary>An expression as it orders: a hierarchyid by its depth-first key, anything else as it is.</summary>
    public PgExpr Ordered(ScalarExpression e, ExpressionContext c) =>
        TypeOf(e, c)?.Name == "hierarchyid" && options.UseCompatSchema
            ? Compat("hierarchyid_key", [Scalar(e, c)], c, e)
            : Scalar(e, c);
}
