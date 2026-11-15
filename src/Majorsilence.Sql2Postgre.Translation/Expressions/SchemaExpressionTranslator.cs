using System.Globalization;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Shared;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Expressions;

/// <param name="Expression">The translation, or null when the expression could not be translated.</param>
public sealed record ExpressionResult(PgExpr? Expression, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Extensions);

/// <summary>
/// Translates the T-SQL expressions found in schema objects — column defaults, check
/// constraints, computed columns and filtered-index predicates — to PostgreSQL. These are a small
/// subset of T-SQL: literals, the table's own columns, operators, <c>CASE</c>, <c>CAST</c> and a
/// handful of built-in functions. Anything else gives <c>S2P3001</c> and no expression; the caller
/// keeps the original as an untranslated comment. (WP 2.2 grows this into the full translator.)
/// </summary>
/// <remarks>
/// SQL Server stores these expressions in its own normalized form (<c>((0))</c>,
/// <c>([qty]&gt;(0))</c>); either that or the form a script writes is accepted. A column's type
/// matters in two places: a <c>bit</c> column is <c>boolean</c> in PostgreSQL, so <c>= 1</c> becomes
/// <c>= true</c>; and <c>+</c> with a string operand is concatenation, <c>||</c> in PostgreSQL.
/// </remarks>
public sealed class SchemaExpressionTranslator(ConversionOptions options, NameMapper names, TypeMapper types)
{
    /// <param name="table">The table the expression belongs to (its columns are the only names in scope).</param>
    /// <param name="columnType">The SQL Server type of a column of that table, by name.</param>
    /// <param name="targetType">For a default: the column's type, so <c>1</c> on a <c>bit</c> column becomes <c>true</c>.</param>
    public ExpressionResult Translate(string tsql, ObjectName table, Func<string, SqlServerType?> columnType,
        SqlServerType? targetType = null, bool isPredicate = false)
    {
        var context = new Context(table, columnType);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        IList<ParseError> errors;
        TSqlFragment? fragment = isPredicate
            ? parser.ParseBooleanExpression(new StringReader(tsql), out errors)
            : parser.ParseExpression(new StringReader(tsql), out errors);
        if (errors.Count > 0 || fragment is null)
            return Fail(context, table, $"'{tsql}' does not parse as a T-SQL expression: {errors.FirstOrDefault()?.Message}");
        try
        {
            PgExpr result = fragment is BooleanExpression b ? Boolean(b, context) : Scalar((ScalarExpression)fragment, context);
            if (targetType?.Name == "bit")
                result = AsBoolean(result);
            return new ExpressionResult(result, context.Diagnostics, context.Extensions.Order(StringComparer.Ordinal).ToList());
        }
        catch (UnsupportedExpressionException ex)
        {
            return Fail(context, table, $"'{tsql}' cannot be translated yet: {ex.Message}");
        }
    }

    /// <summary>
    /// The PostgreSQL type of an expression's value, when the column types and literals it uses
    /// make it evident: arithmetic takes the operand type of highest SQL Server precedence
    /// (float, real, decimal, money, bigint, int, …), and exact decimals become an unconstrained
    /// <c>numeric</c> since SQL Server's result-precision rules do not carry over. Null when unknown.
    /// </summary>
    public string? InferType(string tsql, Func<string, SqlServerType?> columnType)
    {
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).ParseExpression(new StringReader(tsql), out var errors);
        if (errors.Count > 0 || fragment is null)
            return null;
        var type = TypeOf(fragment, new Context(new ObjectName("", ""), columnType));
        return type?.Name switch
        {
            null => null,
            "decimal" or "numeric" when type.Precision is null => "numeric",
            "money" or "smallmoney" => "numeric",
            "nvarchar" or "varchar" when type.Length is null => "text",
            _ => types.Map(type).PgType,
        };
    }

    private static ExpressionResult Fail(Context context, ObjectName table, string message) =>
        new(null, [.. context.Diagnostics, new Diagnostic(DiagnosticCodes.UnsupportedExpression, Severity.Error, table, message)], []);

    private sealed class Context(ObjectName table, Func<string, SqlServerType?> columnType)
    {
        public ObjectName Table { get; } = table;
        public Func<string, SqlServerType?> ColumnType { get; } = columnType;
        public List<Diagnostic> Diagnostics { get; } = [];
        public HashSet<string> Extensions { get; } = new(StringComparer.Ordinal);
    }

    private sealed class UnsupportedExpressionException(string message) : Exception(message);

    private static UnsupportedExpressionException Unsupported(TSqlFragment fragment, string what) =>
        new($"{what} ({Text(fragment)})");

    // ---- boolean expressions ----

    private PgExpr Boolean(BooleanExpression e, Context c) => e switch
    {
        BooleanParenthesisExpression p => Boolean(p.Expression, c),
        BooleanBinaryExpression b => new PgBinary(b.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR",
            Boolean(b.FirstExpression, c), Boolean(b.SecondExpression, c)),
        BooleanNotExpression n => new PgUnary("NOT", Boolean(n.Expression, c)),
        BooleanComparisonExpression cmp => Comparison(cmp, c),
        BooleanIsNullExpression isNull => new PgIsNull(Scalar(isNull.Expression, c), isNull.IsNot),
        InPredicate { Subquery: null } inList => new PgInList(Scalar(inList.Expression, c),
            inList.Values.Select(v => CompareOperand(v, inList.Expression, c)).ToList(), inList.NotDefined),
        BooleanTernaryExpression t when t.TernaryExpressionType is BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween =>
            new PgBetween(Scalar(t.FirstExpression, c), Scalar(t.SecondExpression, c), Scalar(t.ThirdExpression, c),
                t.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween),
        LikePredicate like => Like(like, c),
        _ => throw Unsupported(e, "this predicate"),
    };

    private PgExpr Comparison(BooleanComparisonExpression cmp, Context c)
    {
        string op = cmp.ComparisonType switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
            BooleanComparisonType.NotLessThan => ">=",
            BooleanComparisonType.NotGreaterThan => "<=",
            _ => throw Unsupported(cmp, "this comparison"),
        };
        return new PgBinary(op, CompareOperand(cmp.FirstExpression, cmp.SecondExpression, c),
            CompareOperand(cmp.SecondExpression, cmp.FirstExpression, c));
    }

    /// <summary>An operand compared with a <c>bit</c> column: <c>1</c>/<c>0</c> become <c>true</c>/<c>false</c>.</summary>
    private PgExpr CompareOperand(ScalarExpression operand, ScalarExpression other, Context c)
    {
        var translated = Scalar(operand, c);
        return TypeOf(other, c)?.Name == "bit" ? AsBoolean(translated) : translated;
    }

    /// <summary>
    /// SQL Server's <c>LIKE</c> has no escape character unless one is given; PostgreSQL's default
    /// is backslash. A pattern containing a backslash therefore gets <c>ESCAPE ''</c> so the
    /// backslash stays an ordinary character.
    /// </summary>
    private PgExpr Like(LikePredicate like, Context c)
    {
        var pattern = Scalar(like.SecondExpression, c);
        PgExpr? escape = like.EscapeExpression is null ? null : Scalar(like.EscapeExpression, c);
        if (escape is null && like.SecondExpression is StringLiteral { Value: var text })
        {
            if (text.Contains('\\'))
                escape = PgLiteral.String("");
            if (ContainsCharacterClass(text))
                throw Unsupported(like, "a LIKE pattern with a [ ] character class");
        }
        return new PgLike(Scalar(like.FirstExpression, c), pattern, escape, like.NotDefined);
    }

    private static bool ContainsCharacterClass(string pattern) => pattern.Contains('[');

    // ---- scalar expressions ----

    private PgExpr Scalar(ScalarExpression e, Context c) => e switch
    {
        ParenthesisExpression p => Scalar(p.Expression, c),
        IntegerLiteral i => new PgLiteral(i.Value),
        NumericLiteral n => new PgLiteral(n.Value),
        RealLiteral r => new PgCast(new PgLiteral(r.Value), "double precision"),
        MoneyLiteral m => new PgLiteral(m.Value.TrimStart('$')),
        StringLiteral s => PgLiteral.String(s.Value),
        NullLiteral => PgLiteral.Null,
        BinaryLiteral b => new PgCast(PgLiteral.String("\\x" + b.Value[2..].ToLowerInvariant()), "bytea"),
        ColumnReferenceExpression col => Column(col, c),
        UnaryExpression u => new PgUnary(u.UnaryExpressionType switch
        {
            UnaryExpressionType.Negative => "-",
            UnaryExpressionType.Positive => "+",
            _ => "~",
        }, Scalar(u.Expression, c)),
        BinaryExpression b => Binary(b, c),
        SearchedCaseExpression sc => new PgCase(null,
            sc.WhenClauses.Select(w => new PgWhen(Boolean(w.WhenExpression, c), Scalar(w.ThenExpression, c))).ToList(),
            sc.ElseExpression is null ? null : Scalar(sc.ElseExpression, c)),
        SimpleCaseExpression sc => new PgCase(Scalar(sc.InputExpression, c),
            sc.WhenClauses.Select(w => new PgWhen(Scalar(w.WhenExpression, c), Scalar(w.ThenExpression, c))).ToList(),
            sc.ElseExpression is null ? null : Scalar(sc.ElseExpression, c)),
        IIfCall iif => new PgCase(null, [new PgWhen(Boolean(iif.Predicate, c), Scalar(iif.ThenExpression, c))], Scalar(iif.ElseExpression, c)),
        CoalesceExpression co => new PgFunctionCall("COALESCE", co.Expressions.Select(x => Scalar(x, c)).ToList()),
        NullIfExpression ni => new PgFunctionCall("NULLIF", [Scalar(ni.FirstExpression, c), Scalar(ni.SecondExpression, c)]),
        CastCall cast => Cast(cast.Parameter, cast.DataType, c),
        ConvertCall { Style: null } convert => Cast(convert.Parameter, convert.DataType, c),
        ConvertCall convert => throw Unsupported(convert, "CONVERT with a style"),
        ParameterlessCall p => p.ParameterlessCallType switch
        {
            ParameterlessCallType.CurrentTimestamp => new PgKeyword("localtimestamp"),
            ParameterlessCallType.CurrentUser or ParameterlessCallType.SessionUser
                or ParameterlessCallType.SystemUser or ParameterlessCallType.User => new PgKeyword("current_user"),
            _ => throw Unsupported(p, "this special value"),
        },
        FunctionCall f => Function(f, c),
        _ => throw Unsupported(e, "this kind of expression"),
    };

    private PgExpr Column(ColumnReferenceExpression col, Context c)
    {
        if (col.ColumnType != ColumnType.Regular || col.MultiPartIdentifier is null)
            throw Unsupported(col, "this column reference");
        string name = col.MultiPartIdentifier.Identifiers[^1].Value;
        return new PgColumnRef(names.Column(c.Table, name));
    }

    private PgExpr Binary(BinaryExpression b, Context c)
    {
        var left = Scalar(b.FirstExpression, c);
        var right = Scalar(b.SecondExpression, c);
        if (b.BinaryExpressionType == BinaryExpressionType.Add && (IsString(b.FirstExpression, c) || IsString(b.SecondExpression, c)))
            return new PgBinary("||", left, right);
        string op = b.BinaryExpressionType switch
        {
            BinaryExpressionType.Add => "+",
            BinaryExpressionType.Subtract => "-",
            BinaryExpressionType.Multiply => "*",
            BinaryExpressionType.Divide => "/",
            BinaryExpressionType.Modulo => "%",
            BinaryExpressionType.BitwiseAnd => "&",
            BinaryExpressionType.BitwiseOr => "|",
            BinaryExpressionType.BitwiseXor => "#",
            _ => throw Unsupported(b, "this operator"),
        };
        return new PgBinary(op, left, right);
    }

    private PgExpr Cast(ScalarExpression operand, DataTypeReference type, Context c)
    {
        var mapped = types.Map(ScriptDomTypes.TypeOf(type), c.Table);
        c.Diagnostics.AddRange(mapped.Diagnostics.Where(d => d.Severity != Severity.Info));
        if (mapped.Extension is not null)
            c.Extensions.Add(mapped.Extension);
        var inner = Scalar(operand, c);
        // A bit value cast to boolean from a 0/1 literal is simply true/false.
        if (mapped.PgType == "boolean" && inner is PgLiteral lit && (lit.Sql is "0" or "1"))
            return AsBoolean(inner);
        // SQL Server truncates a decimal or float cast to an integer type (12.5 -> 12, -12.5 -> -12);
        // PostgreSQL rounds it (13, -13). trunc() first gives SQL Server's answer.
        if (mapped.PgType is "smallint" or "integer" or "bigint" && IsFractional(operand, c))
            inner = new PgFunctionCall("trunc", [inner]);
        return new PgCast(inner, mapped.PgType);
    }

    private PgExpr Function(FunctionCall f, Context c)
    {
        if (f.CallTarget is not null || f.OverClause is not null)
            throw Unsupported(f, "a method or windowed call");
        string name = f.FunctionName.Value.ToUpperInvariant();
        var args = f.Parameters;
        PgExpr Arg(int i) => Scalar(args[i], c);
        void Arity(params int[] allowed)
        {
            if (!allowed.Contains(args.Count))
                throw Unsupported(f, $"{name} with {args.Count} arguments");
        }

        switch (name)
        {
            case "GETDATE" or "SYSDATETIME":
                Arity(0);
                return new PgKeyword("localtimestamp");
            case "GETUTCDATE" or "SYSUTCDATETIME":
                Arity(0);
                return new PgAtTimeZone(new PgFunctionCall("now", []), PgLiteral.String("utc"));
            case "SYSDATETIMEOFFSET":
                Arity(0);
                return new PgFunctionCall("now", []);
            case "NEWID":
                Arity(0);
                return new PgFunctionCall("gen_random_uuid", []);
            case "NEWSEQUENTIALID":
                Arity(0);
                if (options.TargetVersion >= 18)
                    return new PgFunctionCall("uuidv7", []);
                c.Diagnostics.Add(new Diagnostic(DiagnosticCodes.NotSequential, Severity.Info, c.Table,
                    "NEWSEQUENTIALID() becomes gen_random_uuid(): values are not ordered. PostgreSQL 18's uuidv7() is time-ordered; target 18 to use it."));
                return new PgFunctionCall("gen_random_uuid", []);
            case "SUSER_SNAME" or "SUSER_NAME" or "USER_NAME" when args.Count == 0:
                return new PgKeyword("current_user");
            case "ISNULL":
                Arity(2);
                return new PgFunctionCall("COALESCE", [Arg(0), Arg(1)]);
            case "UPPER" or "LOWER" or "LTRIM" or "RTRIM":
                Arity(1);
                return new PgFunctionCall(name.ToLowerInvariant(), [Arg(0)]);
            case "ABS":
                Arity(1);
                return new PgFunctionCall("abs", [Arg(0)]);
            case "ROUND":
                Arity(2);
                // PostgreSQL rounds to a number of places only for numeric, not double precision.
                // SQL Server rounds a float's binary value (2.55 is 2.5499…, so ROUND(2.55e0, 1) is
                // 2.5); cast to numeric it is the decimal 2.55, which rounds to 2.6. No core
                // expression reproduces the binary rounding exactly, so say so.
                var x = Arg(0);
                if (TypeOf(args[0], c)?.Name is "float" or "real")
                {
                    x = new PgCast(x, "numeric");
                    c.Diagnostics.Add(new Diagnostic(DiagnosticCodes.FloatRounding, Severity.Warning, c.Table,
                        "ROUND on a float rounds its binary value in SQL Server and its decimal value in PostgreSQL; a value ending in 5 at the rounding digit can round the other way."));
                }
                return new PgFunctionCall("round", [x, Arg(1)]);
            default:
                throw Unsupported(f, $"the function {name}");
        }
    }

    // ---- types ----

    /// <summary>The SQL Server type of an expression where it is evident; null when it is not.</summary>
    private SqlServerType? TypeOf(ScalarExpression e, Context c) => e switch
    {
        ParenthesisExpression p => TypeOf(p.Expression, c),
        UnaryExpression u => TypeOf(u.Expression, c),
        MoneyLiteral => new SqlServerType("money"),
        ColumnReferenceExpression { MultiPartIdentifier: { } id } => c.ColumnType(id.Identifiers[^1].Value),
        StringLiteral { IsNational: true } => new SqlServerType("nvarchar"),
        StringLiteral => new SqlServerType("varchar"),
        IntegerLiteral => new SqlServerType("int"),
        NumericLiteral => new SqlServerType("numeric"),
        RealLiteral => new SqlServerType("float"),
        CastCall cast => ScriptDomTypes.TypeOf(cast.DataType),
        ConvertCall convert => ScriptDomTypes.TypeOf(convert.DataType),
        FunctionCall f when f.FunctionName.Value.ToUpperInvariant() is "UPPER" or "LOWER" or "LTRIM" or "RTRIM" => new SqlServerType("nvarchar"),
        FunctionCall f when f.FunctionName.Value.ToUpperInvariant() is "ISNULL" && f.Parameters.Count > 0 => TypeOf(f.Parameters[0], c),
        CoalesceExpression co when co.Expressions.Count > 0 => TypeOf(co.Expressions[0], c),
        BinaryExpression b when b.BinaryExpressionType == BinaryExpressionType.Add && (IsString(b.FirstExpression, c) || IsString(b.SecondExpression, c)) => new SqlServerType("nvarchar"),
        BinaryExpression b => Wider(TypeOf(b.FirstExpression, c), TypeOf(b.SecondExpression, c)),
        SearchedCaseExpression sc when sc.WhenClauses.Count > 0 => TypeOf(sc.WhenClauses[0].ThenExpression, c),
        SimpleCaseExpression sc when sc.WhenClauses.Count > 0 => TypeOf(sc.WhenClauses[0].ThenExpression, c),
        IIfCall iif => TypeOf(iif.ThenExpression, c),
        FunctionCall f when f.FunctionName.Value.ToUpperInvariant() is "GETDATE" or "SYSDATETIME" or "GETUTCDATE" or "SYSUTCDATETIME" => new SqlServerType("datetime2", Scale: 6),
        FunctionCall f when f.FunctionName.Value.ToUpperInvariant() is "NEWID" or "NEWSEQUENTIALID" => new SqlServerType("uniqueidentifier"),
        FunctionCall f when f.FunctionName.Value.ToUpperInvariant() is "ABS" or "ROUND" && f.Parameters.Count > 0 => TypeOf(f.Parameters[0], c),
        ParameterlessCall { ParameterlessCallType: ParameterlessCallType.CurrentTimestamp } => new SqlServerType("datetime2", Scale: 6),
        _ => null,
    };

    /// <summary>SQL Server's data type precedence among the numeric types: the result of arithmetic on two of them.</summary>
    private static readonly string[] NumericPrecedence = ["float", "real", "decimal", "numeric", "money", "smallmoney", "bigint", "int", "smallint", "tinyint", "bit"];

    private static SqlServerType? Wider(SqlServerType? a, SqlServerType? b)
    {
        if (a is null || b is null)
            return null;
        int ia = Array.IndexOf(NumericPrecedence, a.Name), ib = Array.IndexOf(NumericPrecedence, b.Name);
        if (ia < 0 || ib < 0)
            return null;
        var wider = ia <= ib ? a : b;
        // Two exact types combine to an exact type whose precision SQL Server computes; leave it open.
        return wider.Name is "decimal" or "numeric" ? new SqlServerType("decimal") : wider with { UserTypeName = null };
    }

    /// <summary>
    /// decimal, numeric, float and real values that SQL Server truncates when cast to an integer
    /// type. money and smallmoney are not among them: SQL Server rounds those ($3.75 -> 4), as
    /// PostgreSQL rounds everything.
    /// </summary>
    private bool IsFractional(ScalarExpression e, Context c) =>
        TypeOf(e, c)?.Name is "decimal" or "numeric" or "float" or "real";

    private bool IsString(ScalarExpression e, Context c) =>
        TypeOf(e, c)?.Name is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname";

    /// <summary>A 0/1 literal as false/true; anything else unchanged (a boolean already, or a bit-typed column).</summary>
    private static PgExpr AsBoolean(PgExpr e) => e switch
    {
        PgLiteral { Sql: "1" } => PgLiteral.True,
        PgLiteral { Sql: "0" } => PgLiteral.False,
        _ => e,
    };

    private static string Text(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null)
            return fragment.GetType().Name;
        var sb = new System.Text.StringBuilder();
        for (int i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            sb.Append(fragment.ScriptTokenStream[i].Text);
        return sb.ToString();
    }
}
