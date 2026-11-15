using System.Globalization;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>The built-in functions: each T-SQL function and what it becomes, with SQL Server's behaviour where the two differ.</summary>
public sealed partial class ExpressionTranslator
{
    private PgExpr Function(FunctionCall f, ExpressionContext c)
    {
        // x.f(…) is a user function in schema x, unless x is a column here (then it is a method).
        if (f.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers.Count: 1 } schemaTarget
            && c.Scope.ResolveColumn([schemaTarget.MultiPartIdentifier.Identifiers[0].Value]) is null)
            return UserFunction(f, schemaTarget.MultiPartIdentifier.Identifiers[0].Value, c);
        if (f.CallTarget is ExpressionCallTarget { Expression: ScalarSubquery sq } && f.FunctionName.Value.Equals("value", StringComparison.OrdinalIgnoreCase)
            && XmlValueSubquery is not null)
            return XmlValueSubquery(sq.QueryExpression, c);
        if (f.CallTarget is not null)
            throw MethodCall(f);

        string name = f.FunctionName.Value.ToUpperInvariant();
        var a = f.Parameters;
        PgExpr Arg(int i) => Scalar(a[i], c);
        PgExpr TextArg(int i) => Text(a[i], c);
        void Arity(params int[] allowed)
        {
            if (!allowed.Contains(a.Count))
                throw Untranslatable(f, $"{name} with {a.Count} argument(s)");
        }
        PgFunctionCall Call(string pg, params PgExpr[] args) => new(pg, args);

        if (f.OverClause is not null)
            return Windowed(f, name, c);
        if (IsAggregate(name))
            return Aggregate(f, name, c);
        if (model.ResolveRoutine(SchemaName(f.FunctionName), c.Schema) is { } udf && model.Routine(udf) is not null)
            return UserFunction(f, udf.Schema, c);

        switch (name)
        {
            // ---- dates and times ----
            case "GETDATE" or "SYSDATETIME" or "CURRENT_TIMESTAMP":
                Arity(0);
                return new PgKeyword("localtimestamp");
            case "GETUTCDATE" or "SYSUTCDATETIME":
                Arity(0);
                return new PgAtTimeZone(Call("now"), PgLiteral.String("utc"));
            case "SYSDATETIMEOFFSET":
                Arity(0);
                return Call("now");
            case "DATEADD":
                Arity(3);
                return DateAdd(f, c);
            case "DATEDIFF" or "DATEDIFF_BIG":
                Arity(3);
                return Compat("datediff", [PgLiteral.String(DatePart(a[0], f)), AsTimestamp(a[1], c), AsTimestamp(a[2], c)], c, f);
            case "DATEPART":
                Arity(2);
                return DatePartOf(DatePart(a[0], f), a[1], f, c);
            case "YEAR" or "MONTH" or "DAY":
                Arity(1);
                return DatePartOf(name.ToLowerInvariant(), a[0], f, c);
            case "DATENAME":
                Arity(2);
                return DatePart(a[0], f) switch
                {
                    "month" => Call("to_char", AsTimestamp(a[1], c), PgLiteral.String("FMMonth")),
                    "weekday" => Call("to_char", AsTimestamp(a[1], c), PgLiteral.String("FMDay")),
                    var part => new PgCast(DatePartOf(part, a[1], f, c), "text"),
                };
            case "EOMONTH":
                Arity(1, 2);
                PgExpr months = a.Count == 2 ? new PgBinary("+", Arg(1), new PgLiteral("1")) : new PgLiteral("1");
                return new PgCast(new PgBinary("-",
                    new PgBinary("+", Call("date_trunc", PgLiteral.String("month"), new PgCast(Arg(0), "timestamp")),
                        new PgBinary("*", months, new PgLiteral("interval '1 month'"))),
                    new PgLiteral("interval '1 day'")), "date");
            case "DATEFROMPARTS":
                Arity(3);
                return Call("make_date", Arg(0), Arg(1), Arg(2));
            case "DATETIMEFROMPARTS":
                Arity(7);
                return Call("make_timestamp", Arg(0), Arg(1), Arg(2), Arg(3), Arg(4),
                    new PgBinary("+", Arg(5), new PgBinary("/", Arg(6), new PgLiteral("1000.0"))));
            case "ISDATE":
                Arity(1);
                return Compat("isdate", [new PgCast(TextArg(0), "text")], c, f);

            // ---- strings ----
            case "LEN":
                Arity(1);
                return Call("length", Call("rtrim", TextArg(0)));
            case "DATALENGTH":
                Arity(1);
                if (TypeRules.IsUnicode(TypeOf(a[0], c)))
                    c.Report(DiagnosticCodes.ByteLengthDiffers, Severity.Warning,
                        "DATALENGTH of a Unicode string counts UTF-16 bytes (2 per character) in SQL Server; octet_length counts UTF-8 bytes.");
                return Call("octet_length", Arg(0));
            case "CHARINDEX":
                Arity(2, 3);
                // The helper, even without a start: strpos finds an empty needle at 1, CHARINDEX never finds it (0).
                return Compat("charindex", a.Count == 2 ? [TextArg(0), TextArg(1)] : [TextArg(0), TextArg(1), Arg(2)], c, f);
            case "SUBSTRING":
                Arity(3);
                return Call("substr", TextArg(0), Arg(1), Arg(2));
            case "LEFT" or "RIGHT":
                Arity(2);
                return Call(name.ToLowerInvariant(), TextArg(0), Arg(1));
            case "REPLACE":
                Arity(3);
                return Call("replace", TextArg(0), TextArg(1), TextArg(2));
            case "REVERSE":
                Arity(1);
                return Call("reverse", TextArg(0));
            case "REPLICATE":
                Arity(2);
                return Call("repeat", TextArg(0), Arg(1));
            case "SPACE":
                Arity(1);
                return Call("repeat", PgLiteral.String(" "), Arg(0));
            case "STUFF" when a.Count == 4 && a[1] is IntegerLiteral { Value: "1" } && a[2] is IntegerLiteral:
                // STUFF(s, 1, n, r), the usual way to drop a leading separator: only an empty s is out
                // of range at start 1, and NULLIF turns it into the NULL STUFF gives.
                return new PgTemplate("overlay({0} placing {1} from 1 for {2})", [Call("NULLIF", TextArg(0), PgLiteral.String("")), TextArg(3), Arg(2)]);
            case "STUFF":
                Arity(4);
                // STUFF gives NULL for a start outside the string or a negative length; overlay would not.
                return new PgCase(null, [new PgWhen(
                        new PgBinary("AND", new PgBetween(Arg(1), new PgLiteral("1"), Call("length", TextArg(0))), new PgBinary(">=", Arg(2), new PgLiteral("0"))),
                        new PgTemplate("overlay({0} placing {1} from {2} for {3})", [TextArg(0), TextArg(3), Arg(1), Arg(2)]))], null);
            case "UPPER" or "LOWER":
                Arity(1);
                return Call(name.ToLowerInvariant(), TextArg(0));
            case "LTRIM" or "RTRIM":
                Arity(1, 2);
                return a.Count == 1 ? Call(name.ToLowerInvariant(), TextArg(0)) : Call(name.ToLowerInvariant(), TextArg(0), TextArg(1));
            case "TRIM":
                Arity(1, 2);
                return a.Count == 1 ? Call("btrim", TextArg(0)) : Call("btrim", TextArg(1), TextArg(0));
            case "CONCAT" when c.RequireImmutable && a.All(x => TypeRules.IsString(TypeOf(x, c)) || TypeRules.IsExactNumeric(TypeOf(x, c)) || x is StringLiteral):
                // concat() is only STABLE in PostgreSQL (it formats any type); in a generated column
                // the same result for strings and numbers is coalesce(…, '') || ….
                return a.Select(x => x is StringLiteral ? Scalar(x, c)
                        : Call("COALESCE", TypeRules.IsString(TypeOf(x, c)) ? Scalar(x, c) : new PgCast(Scalar(x, c), "text"), PgLiteral.String("")))
                    .Aggregate((l, r) => new PgBinary("||", l, r));
            case "CONCAT":
                return Call("concat", a.Select((_, i) => Arg(i)).ToArray());
            case "CONCAT_WS":
                return Call("concat_ws", a.Select((_, i) => Arg(i)).ToArray());
            case "ASCII" or "UNICODE":
                Arity(1);
                // ASCII('') is NULL in SQL Server, 0 in PostgreSQL.
                return Call("ascii", Call("NULLIF", TextArg(0), PgLiteral.String("")));
            case "CHAR" or "NCHAR":
                Arity(1);
                return Call("chr", Arg(0));
            case "QUOTENAME":
                Arity(1);
                return new PgBinary("||", new PgBinary("||", PgLiteral.String("["), Call("replace", TextArg(0), PgLiteral.String("]"), PgLiteral.String("]]"))), PgLiteral.String("]"));
            case "STRING_ESCAPE":
                throw Untranslatable(f, "STRING_ESCAPE");
            case "FORMAT":
                Arity(2, 3);
                return Format(f, c);

            // ---- logic and nulls ----
            case "ISNULL":
                Arity(2);
                return new PgFunctionCall("COALESCE", [Branch(a[0], f, c), Branch(a[1], f, c)]);
            case "CHOOSE":
                if (a.Count < 2)
                    throw Untranslatable(f, "CHOOSE without choices");
                return new PgCase(Arg(0), a.Skip(1).Select((x, i) => new PgWhen(new PgLiteral((i + 1).ToString(CultureInfo.InvariantCulture)), Branch(x, f, c))).ToList(), null);
            case "ISNUMERIC":
                Arity(1);
                return Compat("isnumeric", [new PgCast(TextArg(0), "text")], c, f);

            // ---- numbers ----
            case "ABS" or "SIGN" or "EXP" or "SQRT" or "SIN" or "COS" or "TAN" or "ASIN" or "ACOS" or "ATAN" or "DEGREES" or "RADIANS":
                Arity(1);
                return Call(name.ToLowerInvariant(), Arg(0));
            case "CEILING":
                Arity(1);
                return Call("ceil", Arg(0));
            case "FLOOR":
                Arity(1);
                return Call("floor", Arg(0));
            case "ATN2":
                Arity(2);
                return Call("atan2", Arg(0), Arg(1));
            case "SQUARE":
                Arity(1);
                return Call("power", new PgCast(Arg(0), "double precision"), new PgLiteral("2"));
            case "POWER":
                Arity(2);
                // POWER returns the type of its first argument: on an integer the result is truncated to one.
                var power = Call("power", Arg(0), Arg(1));
                return TypeRules.IsInteger(TypeOf(a[0], c))
                    ? new PgCast(Call("trunc", power), types.Map(TypeOf(a[0], c)!).PgType)
                    : power;
            case "LOG":
                Arity(1, 2);
                return a.Count == 1 ? Call("ln", Arg(0)) : Call("log", Arg(1), Arg(0));
            case "LOG10":
                Arity(1);
                return Call("log", Arg(0));
            case "PI":
                Arity(0);
                return Call("pi");
            case "RAND":
                if (a.Count > 0)
                    throw Untranslatable(f, "RAND with a seed");
                return Call("random");
            case "ROUND":
                Arity(2, 3);
                return Round(f, c);

            // ---- identifiers, session, misc ----
            case "NEWID":
                Arity(0);
                return Call("gen_random_uuid");
            case "NEWSEQUENTIALID":
                Arity(0);
                if (options.TargetVersion >= 18)
                    return Call("uuidv7");
                c.Report(DiagnosticCodes.NotSequential, Severity.Info,
                    "NEWSEQUENTIALID() becomes gen_random_uuid(): values are not ordered. PostgreSQL 18's uuidv7() is time-ordered; target 18 to use it.");
                return Call("gen_random_uuid");
            case "SUSER_SNAME" or "SUSER_NAME" or "USER_NAME" or "ORIGINAL_LOGIN" when a.Count == 0:
                return new PgKeyword("current_user");
            case "DB_NAME" when a.Count == 0:
                return Call("current_database");
            case "HOST_NAME":
                Arity(0);
                return new PgCast(Call("inet_client_addr"), "text");
            case "APP_NAME":
                Arity(0);
                return Call("current_setting", PgLiteral.String("application_name"));
            case "SCOPE_IDENTITY" or "IDENT_CURRENT" or "ERROR_MESSAGE" or "ERROR_NUMBER" or "ERROR_LINE"
                or "ERROR_PROCEDURE" or "ERROR_SEVERITY" or "ERROR_STATE" or "XACT_STATE":
                throw Untranslatable(f, $"{name}() in a plain query", DiagnosticCodes.ProceduralGlobal);

            // ---- JSON ----
            // json, not jsonb: SQL Server returns a fragment as written, and jsonb would reformat it.
            case "JSON_VALUE":
                Arity(2);
                return new PgBinary("#>>", new PgCast(Arg(0), "json"), PgLiteral.String(JsonPath(a[1], f)));
            case "JSON_QUERY":
                Arity(1, 2);
                return new PgCast(a.Count == 1 ? new PgCast(Arg(0), "json") : new PgBinary("#>", new PgCast(Arg(0), "json"), PgLiteral.String(JsonPath(a[1], f))), "text");
            case "ISJSON":
                Arity(1);
                return Compat("isjson", [new PgCast(TextArg(0), "text")], c, f);

            // ---- hashing, random bytes, vectors (permitted extensions where core has none) ----
            case "HASHBYTES":
                Arity(2);
                return HashBytes(f, c);
            case "CRYPT_GEN_RANDOM":
                Arity(1);
                return Extension(PermittedExtensions.PgCrypto, Call("gen_random_bytes", Arg(0)), f, c);
            case "VECTOR_DISTANCE":
                Arity(3);
                return VectorDistance(f, c);
            default:
                throw Untranslatable(f, $"the function {f.FunctionName.Value}");
        }
    }

    private static SchemaObjectName SchemaName(Identifier name)
    {
        var s = new SchemaObjectName();
        s.Identifiers.Add(name);
        return s;
    }

    private static UntranslatableException MethodCall(FunctionCall f)
    {
        string method = f.FunctionName.Value.ToUpperInvariant();
        bool spatial = method.StartsWith("ST", StringComparison.Ordinal) || method is "LAT" or "LONG" or "POINT";
        return Untranslatable(f, spatial ? "a spatial method" : "a method call (xml, hierarchyid or spatial)",
            spatial ? DiagnosticCodes.SpatialMethod : DiagnosticCodes.UnsupportedExpression);
    }

    /// <summary>Whether calls to user-defined functions are translated (not in schema expressions, until Stage 3 orders them first).</summary>
    public bool UserFunctions { get; init; } = true;

    /// <summary>A user-defined scalar function: called by its mapped name (it is converted in Stage 3).</summary>
    private PgExpr UserFunction(FunctionCall f, string schema, ExpressionContext c)
    {
        var name = new ObjectName(schema, f.FunctionName.Value);
        if (!UserFunctions)
            throw Untranslatable(f, $"the user-defined function {name} in a table definition (Stage 3 creates functions before the tables that use them)");
        if (model.Routine(name) is null)
            throw Untranslatable(f, $"the function {name}, which is not in this database");
        return new PgFunctionCall(names.Routine(name).Qualified, f.Parameters.Select(p => Scalar(p, c)).ToList());
    }

    private PgExpr Extension(string extension, PgExpr call, TSqlFragment source, ExpressionContext c)
    {
        if (!options.AllowsExtension(extension))
            throw Untranslatable(source, $"this (it needs the {extension} extension, which is disabled)", DiagnosticCodes.ExtensionDisabled);
        c.Extensions.Add(extension);
        return call;
    }

    // ---- aggregates and windows ----

    private static bool IsAggregate(string name) => name is "COUNT" or "COUNT_BIG" or "SUM" or "AVG" or "MIN" or "MAX"
        or "STDEV" or "STDEVP" or "VAR" or "VARP" or "STRING_AGG" or "GROUPING" or "CHECKSUM_AGG" or "APPROX_COUNT_DISTINCT";

    private PgExpr Aggregate(FunctionCall f, string name, ExpressionContext c)
    {
        bool distinct = f.UniqueRowFilter == UniqueRowFilter.Distinct;
        bool star = f.Parameters is [ColumnReferenceExpression { ColumnType: ColumnType.Wildcard }];
        var args = star ? [] : f.Parameters.Select(p => Scalar(p, c)).ToList();
        PgFunctionCall Agg(string pg, IReadOnlyList<PgExpr> arguments) => new(pg, arguments) { Distinct = distinct, Star = star };
        return name switch
        {
            "COUNT" or "COUNT_BIG" => Agg("count", args),
            "SUM" or "MIN" or "MAX" => Agg(name.ToLowerInvariant(), args),
            // AVG of integers is an integer in SQL Server (the average truncated); PostgreSQL returns numeric.
            "AVG" when TypeRules.IsInteger(TypeOf(f.Parameters[0], c)) =>
                new PgCast(new PgFunctionCall("trunc", [Agg("avg", args)]), types.Map(TypeOf(f.Parameters[0], c)!).PgType),
            "AVG" => Agg("avg", args),
            "STDEV" => Agg("stddev_samp", args),
            "STDEVP" => Agg("stddev_pop", args),
            "VAR" => Agg("var_samp", args),
            "VARP" => Agg("var_pop", args),
            "GROUPING" => Agg("GROUPING", args),
            "APPROX_COUNT_DISTINCT" => new PgFunctionCall("count", args) { Distinct = true },
            "STRING_AGG" => new PgFunctionCall("string_agg", [Text(f.Parameters[0], c), args[1]])
            {
                OrderBy = f.WithinGroupClause?.OrderByClause.OrderByElements.Select(o => SortKey(o, c)).ToList() ?? [],
            },
            _ => throw Untranslatable(f, $"the aggregate {name}"),
        };
    }

    private PgExpr Windowed(FunctionCall f, string name, ExpressionContext c)
    {
        var over = Window(f.OverClause!, c);
        if (IsAggregate(name))
            return Aggregate(f, name, c) switch
            {
                PgFunctionCall call => call with { Over = over },
                PgCast { Operand: PgFunctionCall { Name: "trunc", Arguments: [PgFunctionCall avg] } } cast =>
                    cast with { Operand = new PgFunctionCall("trunc", [avg with { Over = over }]) },
                _ => throw Untranslatable(f, "this windowed aggregate"),
            };
        string? pg = name switch
        {
            "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "NTILE" or "LAG" or "LEAD" or "FIRST_VALUE" or "LAST_VALUE" or "CUME_DIST" or "PERCENT_RANK" => name.ToLowerInvariant(),
            _ => null,
        };
        if (pg is null)
            throw Untranslatable(f, $"the window function {name}");
        return new PgFunctionCall(pg, f.Parameters.Select(p => Scalar(p, c)).ToList()) { Over = over };
    }

    private PgWindow Window(OverClause over, ExpressionContext c)
    {
        string? frame = over.WindowFrameClause is { } wf
            ? $"{(wf.WindowFrameType == WindowFrameType.Rows ? "ROWS" : "RANGE")} " + (wf.Bottom is null
                ? Delimiter(wf.Top, c)
                : $"BETWEEN {Delimiter(wf.Top, c)} AND {Delimiter(wf.Bottom, c)}")
            : null;
        return new PgWindow(over.Partitions.Select(p => Scalar(p, c)).ToList(),
            over.OrderByClause?.OrderByElements.Select(o => SortKey(o, c)).ToList() ?? [], frame);
    }

    private string Delimiter(WindowDelimiter d, ExpressionContext c) => d.WindowDelimiterType switch
    {
        WindowDelimiterType.UnboundedPreceding => "UNBOUNDED PRECEDING",
        WindowDelimiterType.UnboundedFollowing => "UNBOUNDED FOLLOWING",
        WindowDelimiterType.CurrentRow => "CURRENT ROW",
        WindowDelimiterType.ValuePreceding => $"{Text(d.OffsetValue)} PRECEDING",
        _ => $"{Text(d.OffsetValue)} FOLLOWING",
    };

    /// <summary>
    /// An ORDER BY key. SQL Server sorts NULLs first ascending and last descending; PostgreSQL the
    /// other way round, so every key says which it wants.
    /// </summary>
    public PgSortKey SortKey(ExpressionWithSortOrder o, ExpressionContext c)
    {
        bool descending = o.SortOrder == SortOrder.Descending;
        return new PgSortKey(Scalar(o.Expression, c), descending, NullsFirst: !descending);
    }

    // ---- dates ----

    /// <summary>A DATEPART/DATEADD/DATEDIFF part, by its canonical name.</summary>
    private static string DatePart(ScalarExpression part, TSqlFragment source)
    {
        // ScriptDom reads the part keyword (day, mm, …) as an IdentifierLiteral.
        string written = part switch
        {
            IdentifierLiteral id => id.Value,
            ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [var id] } => id.Value,
            StringLiteral s => s.Value,
            _ => throw Untranslatable(source, "a date part that is not a keyword"),
        };
        return written.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => "year",
            "quarter" or "qq" or "q" => "quarter",
            "month" or "mm" or "m" => "month",
            "dayofyear" or "dy" or "y" => "dayofyear",
            "day" or "dd" or "d" => "day",
            "week" or "wk" or "ww" => "week",
            "weekday" or "dw" or "w" => "weekday",
            "hour" or "hh" => "hour",
            "minute" or "mi" or "n" => "minute",
            "second" or "ss" or "s" => "second",
            "millisecond" or "ms" => "millisecond",
            "microsecond" or "mcs" => "microsecond",
            "nanosecond" or "ns" => "nanosecond",
            "iso_week" or "isowk" or "isoww" => "iso_week",
            "tzoffset" or "tz" => "tzoffset",
            var other => throw Untranslatable(source, $"the date part '{other}'"),
        };
    }

    /// <summary>A value SQL Server would read as a datetime: a string becomes timestamp; timestamptz is taken in UTC.</summary>
    private PgExpr AsTimestamp(ScalarExpression e, ExpressionContext c)
    {
        var t = TypeOf(e, c);
        var x = Scalar(e, c);
        return t?.Name switch
        {
            "datetimeoffset" => new PgAtTimeZone(x, PgLiteral.String("UTC")),
            "date" or "time" => new PgCast(x, "timestamp"),
            null or "char" or "varchar" or "nchar" or "nvarchar" => new PgCast(x, "timestamp"),
            _ => x,
        };
    }

    private PgExpr DateAdd(FunctionCall f, ExpressionContext c)
    {
        string part = DatePart(f.Parameters[0], f);
        string unit = part switch
        {
            "year" => "1 year",
            "quarter" => "3 months",
            "month" => "1 month",
            "dayofyear" or "day" or "weekday" => "1 day",
            "week" => "7 days",
            "hour" => "1 hour",
            "minute" => "1 minute",
            "second" => "1 second",
            "millisecond" => "1 millisecond",
            "microsecond" => "1 microsecond",
            _ => throw Untranslatable(f, $"DATEADD by {part}"),
        };
        var number = f.Parameters[1];
        PgExpr n = Scalar(number, c);
        // DATEADD truncates a fractional number of units; interval arithmetic would not.
        if (!TypeRules.IsInteger(TypeOf(number, c)) && number is not IntegerLiteral)
            n = new PgFunctionCall("trunc", [n]);
        var dateType = TypeOf(f.Parameters[2], c);
        var date = dateType is null || TypeRules.IsString(dateType) ? new PgCast(Scalar(f.Parameters[2], c), "timestamp") : Scalar(f.Parameters[2], c);
        PgExpr sum = new PgBinary("+", date, new PgBinary("*", n, new PgLiteral($"interval '{unit}'")));
        // A date plus an interval is a timestamp in PostgreSQL; DATEADD on a date returns a date.
        return dateType?.Name == "date" ? new PgCast(sum, "date") : sum;
    }

    private PgExpr DatePartOf(string part, ScalarExpression date, TSqlFragment source, ExpressionContext c)
    {
        var d = AsTimestamp(date, c);
        PgExpr Extract(string field) => new PgCast(new PgTemplate($"extract({field} FROM {{0}})", [d]), "integer");
        switch (part)
        {
            case "year" or "quarter" or "month" or "day" or "hour" or "minute":
                return Extract(part);
            case "dayofyear":
                return Extract("doy");
            case "iso_week":
                return Extract("week");
            case "second":
                return new PgCast(new PgFunctionCall("floor", [new PgTemplate("extract(second FROM {0})", [d])]), "integer");
            case "millisecond":
                return new PgBinary("%", new PgCast(new PgFunctionCall("floor", [new PgTemplate("extract(milliseconds FROM {0})", [d])]), "integer"), new PgLiteral("1000"));
            case "microsecond":
                return new PgBinary("%", new PgCast(new PgTemplate("extract(microseconds FROM {0})", [d]), "integer"), new PgLiteral("1000000"));
            case "weekday":
                c.Report(DiagnosticCodes.DateFirstAssumed, Severity.Info,
                    "DATEPART(weekday) depends on SET DATEFIRST; the translation assumes the default, 7 (Sunday is day 1).");
                return new PgBinary("+", Extract("dow"), new PgLiteral("1"));
            case "week":
                c.Report(DiagnosticCodes.DateFirstAssumed, Severity.Info,
                    "DATEPART(week) depends on SET DATEFIRST; the translation assumes the default, 7 (weeks start on Sunday).");
                return Compat("datepart_week", [d], c, source);
            default:
                throw Untranslatable(source, $"DATEPART({part})");
        }
    }

    // ---- rounding ----

    private PgExpr Round(FunctionCall f, ExpressionContext c)
    {
        var x = Scalar(f.Parameters[0], c);
        if (TypeOf(f.Parameters[0], c)?.Name is "float" or "real")
        {
            x = new PgCast(x, "numeric");
            c.Report(DiagnosticCodes.FloatRounding, Severity.Warning,
                "ROUND on a float rounds its binary value in SQL Server and its decimal value in PostgreSQL; a value ending in 5 at the rounding digit can round the other way.");
        }
        bool truncate = f.Parameters.Count == 3 && f.Parameters[2] is not IntegerLiteral { Value: "0" };
        return new PgFunctionCall(truncate ? "trunc" : "round", [x, Scalar(f.Parameters[1], c)]);
    }

    // ---- JSON ----

    /// <summary>A simple SQL/JSON path (<c>$.a.b[2]</c>, optionally lax) as a PostgreSQL text-array path (<c>{a,b,2}</c>).</summary>
    private static string JsonPath(ScalarExpression path, TSqlFragment source)
    {
        if (path is not StringLiteral { Value: var text })
            throw Untranslatable(source, "a JSON path that is not a literal", DiagnosticCodes.JsonPathUnsupported);
        text = text.Trim();
        if (text.StartsWith("lax ", StringComparison.OrdinalIgnoreCase))
            text = text[4..].TrimStart();
        if (text.StartsWith("strict ", StringComparison.OrdinalIgnoreCase) || !text.StartsWith('$'))
            throw Untranslatable(source, "a strict or non-root JSON path", DiagnosticCodes.JsonPathUnsupported);
        var parts = new List<string>();
        int i = 1;
        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                int start = ++i;
                if (i < text.Length && text[i] == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    parts.Add(text[(i + 1)..end]);
                    i = end + 1;
                    continue;
                }
                while (i < text.Length && text[i] is not ('.' or '['))
                    i++;
                parts.Add(text[start..i]);
            }
            else if (text[i] == '[')
            {
                int end = text.IndexOf(']', i);
                if (end < 0 || !int.TryParse(text.AsSpan(i + 1, end - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw Untranslatable(source, "a JSON path with a wildcard or expression", DiagnosticCodes.JsonPathUnsupported);
                parts.Add(text[(i + 1)..end]);
                i = end + 1;
            }
            else
                throw Untranslatable(source, "this JSON path", DiagnosticCodes.JsonPathUnsupported);
        }
        return "{" + string.Join(',', parts.Select(p => p.Contains(',') || p.Contains('"') || p.Contains(' ') ? "\"" + p.Replace("\"", "\\\"") + "\"" : p)) + "}";
    }

    // ---- hashing and vectors ----

    private PgExpr HashBytes(FunctionCall f, ExpressionContext c)
    {
        if (f.Parameters[0] is not StringLiteral { Value: var algorithm })
            throw Untranslatable(f, "HASHBYTES with an algorithm that is not a literal");
        var input = Scalar(f.Parameters[1], c);
        var type = TypeOf(f.Parameters[1], c);
        if (TypeRules.IsString(type))
        {
            if (TypeRules.IsUnicode(type))
                c.Report(DiagnosticCodes.HashEncodingDiffers, Severity.Warning,
                    "HASHBYTES hashes an nvarchar value's UTF-16 bytes in SQL Server; the translation hashes its UTF-8 bytes, so hashes differ for the same text.");
            input = new PgFunctionCall("convert_to", [input, PgLiteral.String("UTF8")]);
        }
        return algorithm.ToUpperInvariant() switch
        {
            "MD5" => new PgFunctionCall("decode", [new PgFunctionCall("md5", [input]), PgLiteral.String("hex")]),
            "SHA2_256" => new PgFunctionCall("sha256", [input]),
            "SHA2_512" => new PgFunctionCall("sha512", [input]),
            "SHA1" or "SHA" => Extension(PermittedExtensions.PgCrypto, new PgFunctionCall("digest", [input, PgLiteral.String("sha1")]), f, c),
            _ => throw Untranslatable(f, $"HASHBYTES('{algorithm}')"),
        };
    }

    private PgExpr VectorDistance(FunctionCall f, ExpressionContext c)
    {
        if (f.Parameters[0] is not StringLiteral { Value: var metric })
            throw Untranslatable(f, "VECTOR_DISTANCE with a metric that is not a literal");
        var a = Scalar(f.Parameters[1], c);
        var b = Scalar(f.Parameters[2], c);
        PgExpr distance = metric.ToLowerInvariant() switch
        {
            "cosine" => new PgBinary("<=>", a, b),
            "euclidean" => new PgBinary("<->", a, b),
            "dot" => new PgUnary("-", new PgBinary("<#>", a, b)),
            _ => throw Untranslatable(f, $"VECTOR_DISTANCE('{metric}')"),
        };
        return Extension(PermittedExtensions.Vector, distance, f, c);
    }
}
