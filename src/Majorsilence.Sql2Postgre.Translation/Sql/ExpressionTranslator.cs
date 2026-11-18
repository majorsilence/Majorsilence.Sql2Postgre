using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Shared;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Translates T-SQL scalar and boolean expressions to PostgreSQL, using the semantic model for
/// what names are and what types expressions have. The type matters wherever the two engines
/// differ: <c>+</c> on strings is <c>||</c>; a <c>bit</c> is a <c>boolean</c>, so <c>= 1</c> becomes
/// <c>= true</c>; a string compared with a number is converted to the number, as SQL Server does
/// and PostgreSQL will not; a decimal cast to an integer is truncated, not rounded. Anything not
/// handled throws <see cref="UntranslatableException"/> with its diagnostic code.
/// </summary>
public sealed partial class ExpressionTranslator(ConversionOptions options, NameMapper names, TypeMapper types, SemanticModel model)
{
    public ConversionOptions Options => options;
    public NameMapper Names => names;
    public TypeMapper Types => types;
    public SemanticModel Model => model;

    /// <summary>Translates a subquery used as a value or in a predicate; set by the query translator.</summary>
    public Func<QueryExpression, ExpressionContext, PgExpr>? Subquery { get; set; }

    // ---- entry points ----

    public PgExpr Scalar(ScalarExpression e, ExpressionContext c)
    {
        if (e is PrimaryExpression { Collation: { } collation })
            c.Report(DiagnosticCodes.CollationIgnored, Severity.Info,
                $"COLLATE {collation.Value} is dropped; PostgreSQL compares with the column's own collation.");
        return e switch
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
            VariableReference v => Variable(v, c),
            GlobalVariableExpression g => GlobalVariable(g, c),
            UnaryExpression u => Unary(u, c),
            BinaryExpression b => Binary(b, c),
            SearchedCaseExpression sc => SearchedCase(sc, c),
            SimpleCaseExpression sc => SimpleCase(sc, c),
            IIfCall iif => new PgCase(null, [new PgWhen(Boolean(iif.Predicate, c), Branch(iif.ThenExpression, iif, c))], Branch(iif.ElseExpression, iif, c)),
            CoalesceExpression co => new PgFunctionCall("COALESCE", co.Expressions.Select(x => Branch(x, co, c)).ToList()),
            NullIfExpression ni => new PgFunctionCall("NULLIF", [Scalar(ni.FirstExpression, c), Compared(ni.SecondExpression, ni.FirstExpression, c)]),
            CastCall cast => Cast(cast.Parameter, cast.DataType, c),
            ConvertCall convert => Convert(convert, c),
            TryCastCall tc => TryCast(tc.Parameter, tc.DataType, c),
            TryConvertCall tc when tc.Style is null => TryCast(tc.Parameter, tc.DataType, c),
            ParameterlessCall p => Parameterless(p),
            FunctionCall f => Function(f, c),
            LeftFunctionCall l => new PgFunctionCall("left", [Text(l.Parameters[0], c), Scalar(l.Parameters[1], c)]),
            RightFunctionCall r => new PgFunctionCall("right", [Text(r.Parameters[0], c), Scalar(r.Parameters[1], c)]),
            NextValueForExpression n => NextValue(n, c),
            ScalarSubquery sq when Subquery is not null => Subquery(sq.QueryExpression, c),
            UserDefinedTypePropertyAccess p when model.SpatialProperty(p, c.Scope, c.Schema) is { } property => SpatialProperty(property, p, c),
            _ => throw Untranslatable(e, "this kind of expression"),
        };
    }

    public PgExpr Boolean(BooleanExpression e, ExpressionContext c) => e switch
    {
        BooleanParenthesisExpression p => Boolean(p.Expression, c),
        BooleanBinaryExpression b => new PgBinary(b.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR",
            Boolean(b.FirstExpression, c), Boolean(b.SecondExpression, c)),
        BooleanNotExpression n => new PgUnary("NOT", Boolean(n.Expression, c)),
        BooleanComparisonExpression cmp => Comparison(cmp, c),
        BooleanIsNullExpression isNull => new PgIsNull(Scalar(isNull.Expression, c), isNull.IsNot),
        InPredicate { Subquery: null } inList => new PgInList(Scalar(inList.Expression, c),
            inList.Values.Select(v => Compared(v, inList.Expression, c)).ToList(), inList.NotDefined),
        BooleanTernaryExpression t when t.TernaryExpressionType is BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween =>
            new PgBetween(Compared(t.FirstExpression, t.SecondExpression, c), Compared(t.SecondExpression, t.FirstExpression, c),
                Compared(t.ThirdExpression, t.FirstExpression, c), t.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween),
        LikePredicate like => Like(like, c),
        FullTextPredicate ft => FullTextPredicate(ft, c),
        UpdateCall update when ProceduralPredicate?.Invoke(update, c) is { } translated => translated,
        _ => SubqueryPredicate(e, c),
    };

    /// <summary>Predicates only procedural code has (a trigger's UPDATE(column)); set by the procedural translator.</summary>
    public Func<BooleanExpression, ExpressionContext, PgExpr?>? ProceduralPredicate { get; set; }

    /// <summary>EXISTS, IN (subquery) and comparisons with ANY/ALL: the query translator's (WP 2.3) concern.</summary>
    private PgExpr SubqueryPredicate(BooleanExpression e, ExpressionContext c) => e switch
    {
        _ when SubqueryPredicates is { } handler && handler(e, c) is { } translated => translated,
        _ => throw Untranslatable(e, "this predicate"),
    };

    /// <summary>Translates <c>(… FOR XML PATH(''), TYPE).value('.', …)</c>; set by the query translator.</summary>
    public Func<QueryExpression, ExpressionContext, PgExpr>? XmlValueSubquery { get; set; }

    /// <summary>Translates EXISTS / IN (subquery) / ANY / ALL; set by the query translator. Returns null for anything else.</summary>
    public Func<BooleanExpression, ExpressionContext, PgExpr?>? SubqueryPredicates { get; set; }

    public static UntranslatableException Untranslatable(TSqlFragment fragment, string what, string code = DiagnosticCodes.UnsupportedExpression) =>
        new(code, $"{what} cannot be translated yet: {Text(fragment)}");

    // ---- names ----

    private PgExpr Column(ColumnReferenceExpression col, ExpressionContext c)
    {
        if (col.ColumnType != ColumnType.Regular || col.MultiPartIdentifier is null)
            throw Untranslatable(col, col.ColumnType == ColumnType.Wildcard ? "* outside COUNT(*)" : "this pseudo-column");
        var parts = col.MultiPartIdentifier.Identifiers.Select(i => i.Value).ToList();
        if (c.Scope.ResolveColumn(parts) is { } resolved)
        {
            string? qualifier = c.QualifyColumns && parts.Count >= 2 ? AliasFor(resolved.Binding) : null;
            if (c.ReturningPseudoTables && resolved.Binding.Alias is { } pseudo)
                qualifier = pseudo.ToLowerInvariant() switch { "inserted" => "new", "deleted" => "old", _ => qualifier };
            return new PgColumnRef(TargetColumn(resolved.Binding, resolved.Column.Name), qualifier);
        }
        if (model.SpatialProperty(col, c.Scope, c.Schema) is { } property)
            return SpatialProperty(property, col, c);
        c.Report(DiagnosticCodes.UnresolvedColumn, Severity.Warning,
            $"Column {string.Join('.', parts)} could not be resolved; it is written as {names.Alias(parts[^1])} without knowing its type.");
        return new PgColumnRef(names.Alias(parts[^1]), c.QualifyColumns && parts.Count >= 2 ? names.Alias(parts[^2]) : null);
    }

    /// <summary>A column's PostgreSQL name: mapped for a table's or view's own columns, styled for a derived relation's.</summary>
    public string TargetColumn(Binding binding, string column) =>
        binding.Relation is { Name: { } table, Kind: ObjectKind.Table or ObjectKind.View } ? names.Column(table, column) : names.Alias(column);

    /// <summary>
    /// The PostgreSQL name a binding is qualified by: the mapped table name when the T-SQL used the
    /// table's own name, else the mapped alias. The query translator gives FROM items the same.
    /// </summary>
    public string AliasFor(Binding binding) =>
        binding.Relation.Name is { } n && string.Equals(binding.Alias, n.Name, StringComparison.OrdinalIgnoreCase) && binding.Relation.Kind is ObjectKind.Table or ObjectKind.View
            ? names.Relation(n).Name
            : names.Alias(binding.Alias);

    /// <summary>Variables and parameters are procedural code (Stage 3) unless the scope defines how to write them.</summary>
    private PgExpr Variable(VariableReference v, ExpressionContext c) =>
        VariableWriter is { } write && c.Scope.TryVariable(v.Name, out _)
            ? write(v.Name.TrimStart('@'))
            : throw Untranslatable(v, "a variable outside procedural code", DiagnosticCodes.ProceduralGlobal);

    /// <summary>How a variable is written (set by the procedural translator, Stage 3).</summary>
    public Func<string, PgExpr>? VariableWriter { get; set; }

    /// <summary>
    /// How procedural code writes <c>@@ROWCOUNT</c>, <c>@@ERROR</c>, <c>@@IDENTITY</c>… (set by the
    /// procedural translator); null for a global it does not handle.
    /// </summary>
    public Func<string, ExpressionContext, PgExpr?>? ProceduralGlobal { get; set; }

    /// <summary>
    /// How procedural code writes <c>SCOPE_IDENTITY()</c>, <c>ERROR_MESSAGE()</c>… (set by the
    /// procedural translator); null for a function it does not handle.
    /// </summary>
    public Func<string, FunctionCall, ExpressionContext, PgExpr?>? ProceduralFunction { get; set; }

    private PgExpr GlobalVariable(GlobalVariableExpression g, ExpressionContext c) => g.Name.ToUpperInvariant() switch
    {
        var name when ProceduralGlobal?.Invoke(name, c) is { } procedural => procedural,
        "@@SPID" => new PgFunctionCall("pg_backend_pid", []),
        "@@VERSION" => new PgFunctionCall("version", []),
        "@@SERVERNAME" => new PgFunctionCall("current_setting", [PgLiteral.String("cluster_name")]),
        _ => throw Untranslatable(g, $"{g.Name} in a plain query", DiagnosticCodes.ProceduralGlobal),
    };

    private PgExpr NextValue(NextValueForExpression n, ExpressionContext c)
    {
        if (n.OverClause is not null)
            throw Untranslatable(n, "NEXT VALUE FOR … OVER");
        var name = model.Resolve(n.SequenceName, c.Schema, model.IsSequence);
        if (name is null)
            throw Untranslatable(n, "a sequence in another database");
        return new PgFunctionCall("nextval", [PgLiteral.String(names.Relation(name).Qualified)]);
    }

    // ---- operators ----

    private PgExpr Unary(UnaryExpression u, ExpressionContext c)
    {
        var operand = Scalar(u.Expression, c);
        return u.UnaryExpressionType switch
        {
            UnaryExpressionType.Negative when operand is PgLiteral { Sql: [>= '0' and <= '9', ..] } lit => new PgLiteral("-" + lit.Sql),
            UnaryExpressionType.Negative => new PgUnary("-", operand),
            UnaryExpressionType.Positive => operand,
            _ => new PgUnary("~", operand),
        };
    }

    private PgExpr Binary(BinaryExpression b, ExpressionContext c)
    {
        var lt = TypeOf(b.FirstExpression, c);
        var rt = TypeOf(b.SecondExpression, c);
        if (b.BinaryExpressionType == BinaryExpressionType.Add)
        {
            bool ls = TypeRules.IsString(lt), rs = TypeRules.IsString(rt);
            bool ln = TypeRules.IsNumeric(lt), rn = TypeRules.IsNumeric(rt);
            // A string plus a number: SQL Server converts the string to the number's type.
            if (ls && rn || rs && ln)
                return new PgBinary("+", ls ? ToType(b.FirstExpression, rt!, c) : Scalar(b.FirstExpression, c),
                    rs ? ToType(b.SecondExpression, lt!, c) : Scalar(b.SecondExpression, c));
            if (ls || rs)
                return new PgBinary("||", Scalar(b.FirstExpression, c), Scalar(b.SecondExpression, c));
            // A date or datetime plus a number adds days in SQL Server.
            if (TypeRules.IsDateTime(lt) && rn)
                return DatePlusDays(Scalar(b.FirstExpression, c), Scalar(b.SecondExpression, c), lt!);
            if (lt is null && rt is null && !IsNumericLiteral(b.FirstExpression) && !IsNumericLiteral(b.SecondExpression))
                c.Report(DiagnosticCodes.AmbiguousPlus, Severity.Warning,
                    $"The operands of '{Text(b)}' have no known type, so + is kept as addition; if they are strings it must be ||.");
        }
        if (b.BinaryExpressionType == BinaryExpressionType.Subtract && TypeRules.IsDateTime(lt) && TypeRules.IsNumeric(rt))
            return DatePlusDays(Scalar(b.FirstExpression, c), new PgUnary("-", Scalar(b.SecondExpression, c)), lt!);
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
            _ => throw Untranslatable(b, "this operator"),
        };
        return new PgBinary(op, AsRealIfOtherIs(Scalar(b.FirstExpression, c), lt, rt), AsRealIfOtherIs(Scalar(b.SecondExpression, c), rt, lt));
    }

    /// <summary>
    /// An exact number (a decimal literal, an int or money column) against a real: SQL Server
    /// converts it to real and compares or calculates in real; PostgreSQL would convert both
    /// sides to double precision, where real's 0.1 is no longer equal to 0.1.
    /// </summary>
    private static PgExpr AsRealIfOtherIs(PgExpr translated, SqlServerType? ownType, SqlServerType? otherType) =>
        otherType?.Name == "real" && TypeRules.IsExactNumeric(ownType) && ownType!.Name != "bit"
            ? new PgCast(translated, "real")
            : translated;

    private static PgExpr DatePlusDays(PgExpr date, PgExpr days, SqlServerType type) =>
        new PgBinary("+", date, new PgBinary("*", days, new PgLiteral("interval '1 day'")));

    private static bool IsNumericLiteral(ScalarExpression e) =>
        e is IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral || e is UnaryExpression { Expression: IntegerLiteral or NumericLiteral };

    private PgExpr Comparison(BooleanComparisonExpression cmp, ExpressionContext c)
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
            _ => throw Untranslatable(cmp, "this comparison"),
        };
        if (op is "<" or ">" or "<=" or ">=" && TypeOf(cmp.FirstExpression, c)?.Name == "hierarchyid" && TypeOf(cmp.SecondExpression, c)?.Name == "hierarchyid")
            return new PgBinary(op, Ordered(cmp.FirstExpression, c), Ordered(cmp.SecondExpression, c));
        return new PgBinary(op, Compared(cmp.FirstExpression, cmp.SecondExpression, c), Compared(cmp.SecondExpression, cmp.FirstExpression, c));
    }

    /// <summary>
    /// One side of a comparison, converted as SQL Server would convert it to the other side's type:
    /// a 0/1 against a bit is false/true; a string expression against a number, date or uuid is cast
    /// to that type (PostgreSQL has no implicit conversion between them; a string literal it converts itself).
    /// </summary>
    public PgExpr Compared(ScalarExpression operand, ScalarExpression other, ExpressionContext c)
    {
        var translated = Scalar(operand, c);
        var otherType = TypeOf(other, c);
        if (otherType?.Name == "bit")
            return AsBoolean(translated);
        var ownType = TypeOf(operand, c);
        if (TypeRules.IsString(ownType) && operand is not StringLiteral && otherType is not null
            && (TypeRules.IsNumeric(otherType) || TypeRules.IsDateTime(otherType) || otherType.Name == "uniqueidentifier")
            && TypeRules.Rank(otherType.Name) < TypeRules.Rank(ownType!.Name))
            return new PgCast(translated, types.Map(otherType).PgType);
        return AsRealIfOtherIs(translated, ownType, otherType);
    }

    /// <summary>
    /// A CASE/COALESCE/IIF result: 0/1 as booleans when the whole expression is a bit; a bit as an
    /// integer when the whole is a number (SQL Server converts the bit up to the other branches' type).
    /// </summary>
    private PgExpr Branch(ScalarExpression branch, ScalarExpression whole, ExpressionContext c)
    {
        var translated = Scalar(branch, c);
        var wholeType = TypeOf(whole, c);
        if (wholeType?.Name == "bit")
            return AsBoolean(translated);
        if (TypeOf(branch, c)?.Name == "bit" && TypeRules.IsNumeric(wholeType))
            return new PgCast(translated, types.Map(wholeType!).PgType);
        return translated;
    }

    private PgExpr SearchedCase(SearchedCaseExpression sc, ExpressionContext c) => new PgCase(null,
        sc.WhenClauses.Select(w => new PgWhen(Boolean(w.WhenExpression, c), Branch(w.ThenExpression, sc, c))).ToList(),
        sc.ElseExpression is null ? null : Branch(sc.ElseExpression, sc, c));

    private PgExpr SimpleCase(SimpleCaseExpression sc, ExpressionContext c) => new PgCase(Scalar(sc.InputExpression, c),
        sc.WhenClauses.Select(w => new PgWhen(Compared(w.WhenExpression, sc.InputExpression, c), Branch(w.ThenExpression, sc, c))).ToList(),
        sc.ElseExpression is null ? null : Branch(sc.ElseExpression, sc, c));

    /// <summary>
    /// SQL Server's LIKE has no escape character unless one is given; PostgreSQL's default is
    /// backslash, so a pattern with a backslash gets <c>ESCAPE ''</c>. A literal pattern with a
    /// <c>[ ]</c> character class becomes SIMILAR TO, which has bracket expressions (and other
    /// metacharacters, escaped).
    /// </summary>
    private PgExpr Like(LikePredicate like, ExpressionContext c)
    {
        var operand = Text(like.FirstExpression, c);
        if (like.SecondExpression is StringLiteral { Value: var text })
        {
            // SIMILAR TO is a regular expression: on char(n) it would see the padding, which LIKE
            // ignores in both engines. As text the padding is gone.
            if (like.EscapeExpression is null && text.Contains('['))
                return new PgLike(TypeOf(like.FirstExpression, c)?.Name is "char" or "nchar" ? new PgCast(operand, "text") : operand,
                    PgLiteral.String(SimilarPattern(text)), null, like.NotDefined, Similar: true);
            if (like.EscapeExpression is null && text.Contains('\\'))
                return new PgLike(operand, PgLiteral.String(text), PgLiteral.String(""), like.NotDefined);
        }
        return new PgLike(operand, Scalar(like.SecondExpression, c),
            like.EscapeExpression is null ? null : Scalar(like.EscapeExpression, c), like.NotDefined);
    }

    /// <summary>A LIKE pattern with T-SQL character classes as a SIMILAR TO pattern (escape character backslash).</summary>
    public static string SimilarPattern(string like)
    {
        var sb = new StringBuilder();
        bool inClass = false;
        foreach (char ch in like)
        {
            if (inClass)
            {
                if (ch == '\\')
                    sb.Append("\\\\");
                else
                    sb.Append(ch);
                if (ch == ']')
                    inClass = false;
                continue;
            }
            switch (ch)
            {
                case '[':
                    inClass = true;
                    sb.Append(ch);
                    break;
                case '|' or '*' or '+' or '?' or '{' or '}' or '(' or ')' or '\\':
                    sb.Append('\\').Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    // ---- types and conversions ----

    public SqlServerType? TypeOf(ScalarExpression e, ExpressionContext c) => model.TypeOf(e, c.Scope, c.Schema);

    /// <summary>The operand as text: as is when it is a string already, else cast (string functions take only text in PostgreSQL).</summary>
    public PgExpr Text(ScalarExpression e, ExpressionContext c)
    {
        var translated = Scalar(e, c);
        var t = TypeOf(e, c);
        return t is null || TypeRules.IsString(t) || e is StringLiteral or NullLiteral ? translated : new PgCast(translated, "text");
    }

    private PgExpr ToType(ScalarExpression e, SqlServerType type, ExpressionContext c)
    {
        var mapped = types.Map(type);
        return new PgCast(Scalar(e, c), mapped.PgType);
    }

    /// <summary>A 0/1 literal as false/true; anything else unchanged.</summary>
    public static PgExpr AsBoolean(PgExpr e) => e switch
    {
        PgLiteral { Sql: "1" } => PgLiteral.True,
        PgLiteral { Sql: "0" } => PgLiteral.False,
        _ => e,
    };

    /// <summary>
    /// A CAST target: built-in or alias types as the type mapper maps them, with T-SQL's default
    /// length of 30 for character and binary types written without one.
    /// </summary>
    public string CastTarget(DataTypeReference type, ExpressionContext c, out SqlServerType source)
    {
        source = model.TypeOf(type, c.Schema);
        if (type is SqlDataTypeReference { Parameters.Count: 0 } sql && sql.SqlDataTypeOption is SqlDataTypeOption.Char or SqlDataTypeOption.VarChar
                or SqlDataTypeOption.NChar or SqlDataTypeOption.NVarChar or SqlDataTypeOption.Binary or SqlDataTypeOption.VarBinary)
            source = source with { Length = 30 };
        var mapped = types.Map(source with { UserTypeName = null });
        foreach (var d in mapped.Diagnostics.Where(d => d.Severity != Severity.Info))
            c.Diagnostics.Add(d with { Object = c.Owner });
        if (mapped.Extension is not null)
            c.Extensions.Add(mapped.Extension);
        return mapped.PgType;
    }

    private PgExpr Cast(ScalarExpression operand, DataTypeReference type, ExpressionContext c) => CastTo(operand, CastTarget(type, c, out _), c);

    /// <summary>
    /// A value passed or assigned to something of <paramref name="target"/> type (a parameter, a
    /// variable): converted as SQL Server converts it implicitly — the same as CAST — when its own
    /// type is known and differs; as is otherwise.
    /// </summary>
    public PgExpr ConvertTo(ScalarExpression value, SqlServerType target, ExpressionContext c)
    {
        var source = TypeOf(value, c);
        if (value is NullLiteral || source is null || !TypeRules.NeedsConversion(source, target))
            return Scalar(value, c);
        return CastTo(value, types.Map(target with { UserTypeName = null }).PgType, c);
    }

    /// <summary>The operand cast to a PostgreSQL type with T-SQL's CAST semantics.</summary>
    private PgExpr CastTo(ScalarExpression operand, string target, ExpressionContext c)
    {
        var inner = Scalar(operand, c);
        if (target == "boolean" && inner is PgLiteral { Sql: "0" or "1" })
            return AsBoolean(inner);
        // SQL Server truncates a decimal or float cast to an integer type (12.5 -> 12, -12.5 -> -12);
        // PostgreSQL rounds (13, -13). money is the exception: SQL Server rounds it too.
        if (target is "smallint" or "integer" or "bigint" && TypeOf(operand, c)?.Name is "decimal" or "numeric" or "float" or "real")
            inner = new PgFunctionCall("trunc", [inner]);
        // PostgreSQL converts a real to numeric through its 6-significant-digit text form
        // (1441.3699 -> 1441.37); SQL Server converts the binary value. double precision keeps it.
        else if (target.StartsWith("numeric", StringComparison.Ordinal) && TypeOf(operand, c)?.Name == "real")
            inner = new PgCast(inner, "double precision");
        // A bit cast to a number or string goes through integer: PostgreSQL casts boolean only to
        // integer, and to text as 'true'/'false' where SQL Server writes 1/0.
        if (TypeOf(operand, c)?.Name == "bit" && target is not ("boolean" or "integer"))
            inner = new PgCast(inner, "integer");
        return new PgCast(inner, target);
    }

    private PgExpr TryCast(ScalarExpression operand, DataTypeReference type, ExpressionContext c)
    {
        string target = CastTarget(type, c, out _);
        string? helper = target.Split('(')[0] switch
        {
            "smallint" => "try_cast_smallint",
            "integer" => "try_cast_integer",
            "bigint" => "try_cast_bigint",
            "numeric" or "real" or "double precision" => "try_cast_numeric",
            "date" => "try_cast_date",
            "timestamp" or "timestamptz" => "try_cast_timestamp",
            "uuid" => "try_cast_uuid",
            "text" or "varchar" or "char" => null,
            _ => throw Untranslatable(operand, $"TRY_CAST to {target}"),
        };
        var text = Text(operand, c);
        if (helper is null)
            return new PgCast(text, target);
        return new PgCast(Compat(helper, [new PgCast(text, "text")], c, operand), target);
    }

    private static PgExpr Parameterless(ParameterlessCall p) => p.ParameterlessCallType switch
    {
        ParameterlessCallType.CurrentTimestamp => new PgKeyword("localtimestamp"),
        _ => new PgKeyword("current_user"),
    };

    /// <summary>A call to an mssql_compat helper; with the compat schema disabled the construct is untranslatable.</summary>
    public PgExpr Compat(string function, IReadOnlyList<PgExpr> args, ExpressionContext c, TSqlFragment source)
    {
        if (!options.UseCompatSchema)
            throw Untranslatable(source, $"this (it needs the mssql_compat helper {function}, and the compat schema is disabled)", DiagnosticCodes.CompatDisabled);
        c.CompatFunctions.Add(function);
        return new PgFunctionCall($"{NameMapper.Quote(options.CompatSchemaName)}.{function}", args);
    }

    public static string Text(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null || fragment.FirstTokenIndex < 0)
            return fragment.GetType().Name;
        var sb = new StringBuilder();
        for (int i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            sb.Append(fragment.ScriptTokenStream[i].Text);
        return sb.ToString();
    }
}
