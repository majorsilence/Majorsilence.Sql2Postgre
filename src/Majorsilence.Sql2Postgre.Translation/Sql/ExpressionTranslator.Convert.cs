using System.Globalization;
using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>CONVERT with a style, and FORMAT: date and time text in SQL Server's fixed layouts, as to_char / to_timestamp patterns.</summary>
public sealed partial class ExpressionTranslator
{
    /// <summary>
    /// The date and time styles of CONVERT (from the T-SQL reference's CAST and CONVERT page) as
    /// PostgreSQL template patterns. Styles below 100 are the same layouts with a two-digit year.
    /// </summary>
    private static string? StylePattern(int style, SqlServerType? dateType)
    {
        string fraction = dateType?.Name == "datetime2" || dateType?.Name == "datetimeoffset" || dateType?.Name == "time"
            ? $"FF{Math.Min(dateType.Scale ?? 7, 6)}" : "MS";
        return style switch
        {
            1 => "MM/DD/YY",
            101 => "MM/DD/YYYY",
            2 => "YY.MM.DD",
            102 => "YYYY.MM.DD",
            3 => "DD/MM/YY",
            103 => "DD/MM/YYYY",
            4 => "DD.MM.YY",
            104 => "DD.MM.YYYY",
            5 => "DD-MM-YY",
            105 => "DD-MM-YYYY",
            6 => "DD Mon YY",
            106 => "DD Mon YYYY",
            7 => "Mon DD, YY",
            107 => "Mon DD, YYYY",
            10 => "MM-DD-YY",
            110 => "MM-DD-YYYY",
            11 => "YY/MM/DD",
            111 => "YYYY/MM/DD",
            12 => "YYMMDD",
            112 => "YYYYMMDD",
            23 => "YYYY-MM-DD",
            20 or 120 => "YYYY-MM-DD HH24:MI:SS",
            21 or 25 or 121 => $"YYYY-MM-DD HH24:MI:SS.{fraction}",
            126 => $"YYYY-MM-DD\"T\"HH24:MI:SS.{fraction}",
            8 or 108 or 24 => "HH24:MI:SS",
            14 or 114 => "HH24:MI:SS:MS",
            _ => null,
        };
    }

    private PgExpr Convert(ConvertCall convert, ExpressionContext c)
    {
        if (convert.Style is null)
            return Cast(convert.Parameter, convert.DataType, c);
        if (convert.Style is not IntegerLiteral { Value: var styleText })
            throw Untranslatable(convert, "CONVERT with a style that is not a literal", DiagnosticCodes.ConvertStyleUnsupported);
        int style = int.Parse(styleText, CultureInfo.InvariantCulture);
        string target = CastTarget(convert.DataType, c, out var targetType);
        var sourceType = TypeOf(convert.Parameter, c);

        // Style 0 (and 100 for dates is not 0) is the default conversion for every type.
        if (style == 0 && !TypeRules.IsDateTime(sourceType))
            return Cast(convert.Parameter, convert.DataType, c);

        if (TypeRules.IsString(targetType) && TypeRules.IsDateTime(sourceType))
        {
            string pattern = StylePattern(style, sourceType)
                ?? throw Untranslatable(convert, $"CONVERT style {style}", DiagnosticCodes.ConvertStyleUnsupported);
            PgExpr text = new PgFunctionCall("to_char", [Scalar(convert.Parameter, c), PgLiteral.String(pattern)]);
            // Style 126 leaves out a fraction of zero; datetime2(7) has a seventh digit PostgreSQL does not keep.
            if (style == 126)
                text = new PgFunctionCall("regexp_replace", [text, PgLiteral.String("\\.0+$"), PgLiteral.String("")]);
            else if (sourceType?.Scale == 7 && style is 21 or 25 or 121)
                text = new PgBinary("||", text, PgLiteral.String("0"));
            return new PgCast(text, target);
        }

        if (TypeRules.IsDateTime(targetType) && (TypeRules.IsString(sourceType) || convert.Parameter is StringLiteral))
        {
            string pattern = StylePattern(style, targetType)
                ?? throw Untranslatable(convert, $"CONVERT style {style}", DiagnosticCodes.ConvertStyleUnsupported);
            var value = Text(convert.Parameter, c);
            PgExpr styled = targetType.Name == "date"
                ? new PgFunctionCall("to_date", [value, PgLiteral.String(pattern)])
                : new PgCast(new PgFunctionCall("to_timestamp", [value, PgLiteral.String(pattern)]), target);
            return IsoUnderStyle(value, style, pattern, targetType, target, styled);
        }

        // money and float to text: 1 adds thousands separators, 2 four decimal places; 0 is the default.
        if (TypeRules.IsString(targetType) && sourceType?.Name is "money" or "smallmoney" && style is 1 or 2)
            return new PgCast(new PgFunctionCall("to_char", [Scalar(convert.Parameter, c),
                PgLiteral.String(style == 1 ? "FM999,999,999,999,990.00" : "FM999999999999990.0000")]), target);

        throw Untranslatable(convert, $"CONVERT from {sourceType?.ToString() ?? "an unknown type"} to {targetType} with style {style}", DiagnosticCodes.ConvertStyleUnsupported);
    }

    /// <summary>
    /// SQL Server also reads some year-first strings under a style that is not year-first, as
    /// established on SQL Server 2022: <c>yyyymmdd</c> always; for datetime and smalldatetime,
    /// <c>yyyy-a-b</c> (with a time or not) as year, then month and day in the style's order (so
    /// <c>2000-06-01</c> is 6 January under 103); for date and datetime2, ISO 8601 with a
    /// <c>T</c>. Each is tried before the style's own layout. The casts go through text, so a
    /// literal in a branch not taken is not converted when the query is planned.
    /// </summary>
    private static PgExpr IsoUnderStyle(PgExpr value, int style, string pattern, SqlServerType targetType, string target, PgExpr styled)
    {
        if (pattern.StartsWith("YYYY", StringComparison.Ordinal) || pattern.StartsWith("HH", StringComparison.Ordinal))
            return styled;
        PgWhen When(string regex, PgExpr result) => new(new PgBinary("~", value, PgLiteral.String(regex)), result);
        PgExpr Parse(string layout) => targetType.Name == "date"
            ? new PgFunctionCall("to_date", [value, PgLiteral.String(layout)])
            : new PgCast(new PgFunctionCall("to_timestamp", [value, PgLiteral.String(layout)]), target);
        var whens = new List<PgWhen> { When("^[0-9]{8}$", Parse("YYYYMMDD")) };
        if (targetType.Name is "datetime" or "smalldatetime")
        {
            bool dayFirst = style is 3 or 103 or 4 or 104 or 5 or 105;
            whens.Add(When("^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}( |$)", Parse(dayFirst ? "YYYY-DD-MM HH24:MI:SS.MS" : "YYYY-MM-DD HH24:MI:SS.MS")));
        }
        else if (targetType.Name is "date" or "datetime2")
            whens.Add(When("^[0-9]{4}-[0-9]{2}-[0-9]{2}T", new PgCast(new PgCast(value, "text"), target)));
        return new PgCase(null, whens, styled);
    }

    /// <summary>FORMAT with a custom date/time pattern (.NET format strings) or N/F number formats, in the invariant or en-US culture.</summary>
    private PgExpr Format(FunctionCall f, ExpressionContext c)
    {
        if (f.Parameters.Count == 3 && f.Parameters[2] is not StringLiteral { Value: "en-US" or "en-us" or "" })
            throw Untranslatable(f, "FORMAT with a culture", DiagnosticCodes.FormatUnsupported);
        if (f.Parameters[1] is not StringLiteral { Value: var format })
            throw Untranslatable(f, "FORMAT with a format that is not a literal", DiagnosticCodes.FormatUnsupported);
        var valueType = TypeOf(f.Parameters[0], c);
        var value = Scalar(f.Parameters[0], c);

        if (TypeRules.IsNumeric(valueType) && format.Length is >= 1 and <= 3 && format[0] is 'N' or 'n' or 'F' or 'f'
            && (format.Length == 1 || int.TryParse(format.AsSpan(1), out _)))
        {
            int decimals = format.Length == 1 ? 2 : int.Parse(format.AsSpan(1), CultureInfo.InvariantCulture);
            string integer = format[0] is 'N' or 'n' ? "FM999,999,999,999,999,990" : "FM999999999999999990";
            string pattern = decimals == 0 ? integer : integer + "." + new string('0', decimals);
            // .NET rounds half away from zero; to_char on a double rounds half to even, on numeric away from zero.
            if (valueType?.Name is "float" or "real")
                value = new PgCast(value, "numeric");
            return new PgFunctionCall("to_char", [value, PgLiteral.String(pattern)]);
        }

        if (TypeRules.IsDateTime(valueType) && format.Length > 1)
            return new PgFunctionCall("to_char", [value, PgLiteral.String(DatePattern(format, f))]);

        throw Untranslatable(f, $"FORMAT('{format}')", DiagnosticCodes.FormatUnsupported);
    }

    /// <summary>
    /// A .NET custom date and time format as a PostgreSQL to_char template. Separators are copied as
    /// they are; literal letters and digits are double-quoted so to_char cannot read them as patterns.
    /// </summary>
    public static string DatePattern(string format, TSqlFragment source)
    {
        var sb = new StringBuilder();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
            {
                string text = literal.ToString();
                if (text.Any(char.IsLetterOrDigit) || text.Contains('"'))
                    sb.Append('"').Append(text.Replace("\"", "\\\"")).Append('"');
                else
                    sb.Append(text);
                literal.Clear();
            }
        }
        int i = 0;
        while (i < format.Length)
        {
            char ch = format[i];
            int run = 1;
            while (i + run < format.Length && format[i + run] == ch)
                run++;
            string? token = (ch, run) switch
            {
                ('y', 4) or ('y', 5) => "YYYY",
                ('y', 2) => "YY",
                ('M', >= 4) => "FMMonth",
                ('M', 3) => "Mon",
                ('M', 2) => "MM",
                ('M', 1) => "FMMM",
                ('d', >= 4) => "FMDay",
                ('d', 3) => "Dy",
                ('d', 2) => "DD",
                ('d', 1) => "FMDD",
                ('H', 2) => "HH24",
                ('H', 1) => "FMHH24",
                ('h', 2) => "HH12",
                ('h', 1) => "FMHH12",
                ('m', 2) => "MI",
                ('m', 1) => "FMMI",
                ('s', 2) => "SS",
                ('s', 1) => "FMSS",
                ('f', 3) => "MS",
                ('f', >= 1 and <= 6) => $"FF{run}",
                ('t', 2) => "AM",
                _ when char.IsLetter(ch) => throw Untranslatable(source, $"the format specifier '{new string(ch, run)}'", DiagnosticCodes.FormatUnsupported),
                _ => null,
            };
            if (token is null)
            {
                if (ch == '\\' && i + 1 < format.Length)
                {
                    literal.Append(format[i + 1]);
                    i += 2;
                    continue;
                }
                literal.Append(ch, run);
            }
            else
            {
                Flush();
                sb.Append(token);
            }
            i += run;
        }
        Flush();
        return sb.ToString();
    }
}
