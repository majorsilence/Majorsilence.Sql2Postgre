using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Translation.Semantics;

/// <summary>
/// SQL Server's rules for the type of an expression, as the T-SQL reference documents them
/// ("Data type precedence"): when an operator combines two types, the one of higher precedence
/// wins and the other is converted to it.
/// </summary>
public static class TypeRules
{
    /// <summary>Highest precedence first. User-defined (alias) types rank above everything; they are resolved to their base first.</summary>
    private static readonly string[] Precedence =
    [
        "sql_variant", "xml", "datetimeoffset", "datetime2", "datetime", "smalldatetime", "date", "time",
        "float", "real", "decimal", "numeric", "money", "smallmoney", "bigint", "int", "smallint", "tinyint", "bit",
        "ntext", "text", "image", "timestamp", "uniqueidentifier", "nvarchar", "nchar", "varchar", "char", "varbinary", "binary",
    ];

    public static int Rank(string type)
    {
        string name = type == "sysname" ? "nvarchar" : type == "rowversion" ? "timestamp" : type;
        int i = Array.IndexOf(Precedence, name);
        return i < 0 ? int.MaxValue : i;
    }

    public static bool IsString(SqlServerType? t) => t?.Name is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname";

    public static bool IsUnicode(SqlServerType? t) => t?.Name is "nchar" or "nvarchar" or "ntext" or "sysname";

    public static bool IsExactNumeric(SqlServerType? t) => t?.Name is "decimal" or "numeric" or "money" or "smallmoney" or "bigint" or "int" or "smallint" or "tinyint" or "bit";

    public static bool IsInteger(SqlServerType? t) => t?.Name is "bigint" or "int" or "smallint" or "tinyint";

    public static bool IsNumeric(SqlServerType? t) => IsExactNumeric(t) || t?.Name is "float" or "real";

    public static bool IsDateTime(SqlServerType? t) => t?.Name is "datetime" or "datetime2" or "smalldatetime" or "date" or "time" or "datetimeoffset";

    /// <summary>
    /// Whether a value of <paramref name="source"/> type passed or assigned to <paramref name="target"/>
    /// needs an explicit conversion in PostgreSQL to behave as in SQL Server: not when the types are
    /// the same, a string goes to a string at least as long, or an integer to a wider integer or a
    /// decimal or float (PostgreSQL widens those itself).
    /// </summary>
    public static bool NeedsConversion(SqlServerType source, SqlServerType target)
    {
        // A table variable passed to a table-valued parameter is an array of the table type already.
        if (source.Name == "table")
            return false;
        if (string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase)
            && source.Length == target.Length && source.Precision == target.Precision && source.Scale == target.Scale)
            return false;
        if (IsString(source) && IsString(target))
            return !(target.IsMax || target.Length is null || source.Length is int sl && sl > 0 && target.Length >= sl);
        if (IsInteger(source) && IsInteger(target))
            return IntegerRank(target.Name) < IntegerRank(source.Name);
        if (IsInteger(source) && IsNumeric(target) && target.Name != "bit")
            return false;
        return true;
    }

    private static int IntegerRank(string name) => name switch { "tinyint" => 1, "smallint" => 2, "int" => 3, _ => 4 };

    /// <summary>The type two operand types combine to: the one of higher precedence (null if either is unknown).</summary>
    public static SqlServerType? Wider(SqlServerType? a, SqlServerType? b)
    {
        if (a is null || b is null)
            return null;
        return Rank(a.Name) <= Rank(b.Name) ? a : b;
    }

    /// <summary>
    /// The type of a value that may come from either of two expressions (CASE branches, COALESCE,
    /// UNION columns): the higher precedence type; for strings the longer length; for two different
    /// exact decimals a decimal of open precision, since SQL Server widens it to hold both.
    /// </summary>
    public static SqlServerType? Combine(SqlServerType? a, SqlServerType? b)
    {
        var wider = Wider(a, b);
        if (wider is null)
            return null;
        // Two strings: Unicode if either is, varying if either is, with the longer length. So
        // CASE … THEN 'ALL' ELSE char12 END is varchar(12) and nchar(5) with varchar(10) is
        // nvarchar(10), as SQL Server makes them (text and ntext keep their precedence).
        if (IsString(a) && IsString(b))
        {
            int length = a!.IsMax || b!.IsMax ? -1 : Math.Max(a.Length ?? 1, b.Length ?? 1);
            if (wider.Name is "text" or "ntext")
                return wider with { Length = length, UserTypeName = null };
            bool unicode = IsUnicode(a) || IsUnicode(b);
            bool fixedLength = a.Name is "char" or "nchar" && b!.Name is "char" or "nchar";
            return new SqlServerType((unicode ? "n" : "") + (fixedLength ? "char" : "varchar"), length);
        }
        return Arithmetic(a, b);
    }

    /// <summary>
    /// The result of arithmetic: the wider type, except that two exact decimals give a decimal whose
    /// precision SQL Server computes from both; that precision is left open here.
    /// </summary>
    public static SqlServerType? Arithmetic(SqlServerType? a, SqlServerType? b)
    {
        var wider = Wider(a, b);
        if (wider is null)
            return null;
        if (wider.Name is "decimal" or "numeric" && (a?.Name != b?.Name || a?.Precision != b?.Precision || a?.Scale != b?.Scale))
            return new SqlServerType("decimal");
        return wider with { UserTypeName = null };
    }

    /// <summary>The result of <c>+</c> on two strings: Unicode if either is, and <c>max</c> length (SQL Server adds the lengths; the sum is not tracked).</summary>
    public static SqlServerType Concatenation(SqlServerType? a, SqlServerType? b) =>
        IsUnicode(a) || IsUnicode(b) ? new SqlServerType("nvarchar", -1) : new SqlServerType("varchar", -1);

    /// <summary>
    /// Return types of the built-in functions whose result does not depend on their arguments, or
    /// depends on them simply. Null when the function is unknown or the type depends on more.
    /// </summary>
    /// <summary>
    /// SUM and AVG's result type: int for the smaller integers, bigint for bigint, decimal(38, s)
    /// for a decimal (AVG's scale is at least 6), money for either money type, float for float and real.
    /// </summary>
    public static SqlServerType? SumOrAverage(SqlServerType? t, bool average) => t?.Name switch
    {
        "tinyint" or "smallint" or "int" => new SqlServerType("int"),
        "decimal" or "numeric" => new SqlServerType("decimal", Precision: 38, Scale: average ? Math.Max(t.Scale ?? 0, 6) : t.Scale ?? 0),
        "money" or "smallmoney" => new SqlServerType("money"),
        "real" or "float" => new SqlServerType("float", Precision: 53),
        _ => t,
    };

    public static SqlServerType? BuiltinReturnType(string upperName, IReadOnlyList<SqlServerType?> args)
    {
        SqlServerType? First() => args.Count > 0 ? args[0] : null;
        SqlServerType StringLike() => IsUnicode(First()) ? new SqlServerType("nvarchar", -1) : new SqlServerType("varchar", -1);
        return upperName switch
        {
            "GETDATE" or "GETUTCDATE" or "CURRENT_TIMESTAMP" => new SqlServerType("datetime"),
            "SYSDATETIME" or "SYSUTCDATETIME" => new SqlServerType("datetime2", Scale: 7),
            "SYSDATETIMEOFFSET" => new SqlServerType("datetimeoffset", Scale: 7),
            "NEWID" or "NEWSEQUENTIALID" => new SqlServerType("uniqueidentifier"),
            "LEN" or "DATALENGTH" or "CHARINDEX" or "PATINDEX" or "DATEDIFF" or "DATEPART" or "YEAR" or "MONTH" or "DAY"
                or "ISNUMERIC" or "ISDATE" or "ISJSON" or "SIGN" or "UNICODE" or "ASCII" or "@@ROWCOUNT" or "@@ERROR" or "@@FETCH_STATUS" => new SqlServerType("int"),
            "COUNT" or "ERROR_NUMBER" => new SqlServerType("int"),
            "COUNT_BIG" or "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "NTILE" or "DATEDIFF_BIG" => new SqlServerType("bigint"),
            "SCOPE_IDENTITY" or "@@IDENTITY" or "IDENT_CURRENT" => new SqlServerType("decimal", Precision: 38, Scale: 0),
            "UPPER" or "LOWER" or "LTRIM" or "RTRIM" or "TRIM" or "LEFT" or "RIGHT" or "SUBSTRING" or "REPLACE" or "REVERSE"
                or "REPLICATE" or "STUFF" or "STRING_AGG" or "CONCAT_WS" => StringLike(),
            "CONCAT" => args.Any(IsUnicode) ? new SqlServerType("nvarchar", -1) : new SqlServerType("varchar", -1),
            "DATENAME" or "FORMAT" or "ERROR_MESSAGE" or "ERROR_PROCEDURE" or "JSON_VALUE" or "SUSER_SNAME" or "SUSER_NAME"
                or "USER_NAME" or "DB_NAME" or "HOST_NAME" or "APP_NAME" or "QUOTENAME" or "CHAR" or "NCHAR" or "SPACE" or "STR" => new SqlServerType("nvarchar", 4000),
            "JSON_QUERY" or "JSON_MODIFY" => new SqlServerType("nvarchar", -1),
            "ISNULL" => First(),
            "COALESCE" => args.Aggregate((SqlServerType?)null, (acc, t) => acc is null ? t : Combine(acc, t)),
            "NULLIF" => First(),
            "ABS" or "ROUND" or "CEILING" or "FLOOR" or "MIN" or "MAX" => First(),
            "SUM" => SumOrAverage(First(), average: false),
            "AVG" => SumOrAverage(First(), average: true),
            "POWER" or "SQUARE" or "SQRT" or "EXP" or "LOG" or "LOG10" or "PI" or "RAND" or "SIN" or "COS" or "TAN" or "ATAN" => new SqlServerType("float", Precision: 53),
            "EOMONTH" or "DATEFROMPARTS" => new SqlServerType("date"),
            "DATEADD" => args.Count > 2 ? args[2] : null,
            "DATETIMEFROMPARTS" => new SqlServerType("datetime"),
            "DATETIME2FROMPARTS" => new SqlServerType("datetime2", Scale: 7),
            "IIF" or "CHOOSE" => args.Skip(upperName == "IIF" ? 0 : 1).Aggregate((SqlServerType?)null, (acc, t) => acc is null ? t : Combine(acc, t)),
            "ERROR_SEVERITY" or "ERROR_STATE" or "ERROR_LINE" or "XACT_STATE" or "@@TRANCOUNT" or "@@SPID" => new SqlServerType("int"),
            _ => null,
        };
    }
}
