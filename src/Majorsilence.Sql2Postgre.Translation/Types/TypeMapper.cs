using System.Globalization;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;

namespace Majorsilence.Sql2Postgre.Translation.Types;

/// <param name="PgType">The PostgreSQL type, as it is written in DDL.</param>
/// <param name="Extension">The permitted extension the type needs, if any.</param>
public sealed record TypeMapping(string PgType, IReadOnlyList<Diagnostic> Diagnostics, string? Extension = null);

/// <summary>
/// Maps a SQL Server column type to the PostgreSQL type that holds the same values. Core types
/// only, except PostGIS for spatial and pgvector for <c>vector</c> when those extensions are
/// enabled; with them disabled the values are kept in a core type (WKB in <c>bytea</c>, a
/// <c>real[]</c> array) and a warning says so.
/// </summary>
public sealed class TypeMapper(ConversionOptions options, NameMapper names)
{
    /// <summary>PostgreSQL's largest <c>varchar(n)</c>; beyond it only <c>text</c> holds the value.</summary>
    public const int MaxVarcharLength = 10_485_760;

    /// <summary>PostgreSQL keeps at most microseconds; SQL Server's 7th fractional digit (100 ns) does not fit.</summary>
    public const int MaxFractionalDigits = 6;

    public TypeMapping Map(SqlServerType type, ObjectName table, string column)
    {
        if (options.TypeOverrides.TryGetValue($"{table.Schema}.{table.Name}.{column}", out string? overridden)
            || TryOverrideIgnoringCase($"{table.Schema}.{table.Name}.{column}", out overridden))
            return new TypeMapping(overridden!, []);
        var mapping = Map(type, table);
        return type.Collation is { } collation && IsCaseInsensitive(collation) && IsText(mapping.PgType)
               && options.CaseInsensitive == CaseInsensitiveStrategy.Report
            ? mapping with
            {
                Diagnostics = [.. mapping.Diagnostics, new Diagnostic(DiagnosticCodes.CaseInsensitiveCollation, Severity.Info, table,
                    $"{table}.{column} uses the case-insensitive collation {collation}; PostgreSQL compares it case-sensitively.")],
            }
            : mapping;
    }

    /// <summary>The type alone, without overrides or collation notes (also used for variables and parameters).</summary>
    public TypeMapping Map(SqlServerType type, ObjectName? owner = null)
    {
        if (type.UserTypeName is { } alias)
        {
            int dot = alias.IndexOf('.');
            var domain = names.Type(new ObjectName(alias[..dot], alias[(dot + 1)..]));
            return new TypeMapping(domain.Qualified, []);
        }

        string Fraction(string pg, int digits) => $"{pg}({Math.Min(digits, MaxFractionalDigits)})";
        List<Diagnostic> FractionNote(int? scale) => scale > MaxFractionalDigits
            ? [new Diagnostic(DiagnosticCodes.FractionalSecondsReduced, Severity.Info, owner,
                $"{type} keeps 100-nanosecond precision; PostgreSQL keeps microseconds, so values are rounded.")]
            : [];
        Diagnostic Warn(string code, string message) => new(code, Severity.Warning, owner, message);

        int scale = type.Scale ?? 7;
        return type.Name switch
        {
            "bit" => Plain("boolean"),
            "tinyint" or "smallint" => Plain("smallint"),
            "int" => Plain("integer"),
            "bigint" => Plain("bigint"),
            "decimal" or "numeric" => Plain($"numeric({type.Precision ?? 18},{type.Scale ?? 0})"),
            "money" => Plain("numeric(19,4)"),
            "smallmoney" => Plain("numeric(10,4)"),
            "real" => Plain("real"),
            "float" => Plain((type.Precision ?? 53) <= 24 ? "real" : "double precision"),
            "char" or "nchar" => Plain($"char({Length(type, 1)})"),
            "varchar" or "nvarchar" => Plain(type.IsMax || (type.Length ?? 1) > MaxVarcharLength ? "text" : $"varchar({Length(type, 1)})"),
            "text" or "ntext" => Plain("text"),
            "binary" or "varbinary" or "image" => Plain("bytea"),
            "date" => Plain("date"),
            "time" => new TypeMapping(Fraction("time", scale), FractionNote(scale)),
            "datetime" => Plain("timestamp(3)"),
            "smalldatetime" => Plain("timestamp(0)"),
            "datetime2" => new TypeMapping(Fraction("timestamp", scale), FractionNote(scale)),
            "datetimeoffset" => new TypeMapping(Fraction("timestamptz", scale),
                [.. FractionNote(scale), Warn(DiagnosticCodes.OffsetNotPreserved,
                    $"{type} keeps each value's UTC offset; timestamptz keeps the instant only, so the original offset is lost.")]),
            "uniqueidentifier" => Plain("uuid"),
            "xml" => Plain("xml"),
            "sysname" => Plain("varchar(128)"),
            "timestamp" or "rowversion" => new TypeMapping("bytea", [Warn(DiagnosticCodes.RowversionSemantics,
                "rowversion is set by SQL Server on every change; in PostgreSQL it is an ordinary bytea column. Optimistic-concurrency checks that read it need a trigger or xmin.")]),
            "sql_variant" => new TypeMapping("text", [Warn(DiagnosticCodes.SqlVariantAsText,
                "sql_variant holds values of many types; it is kept as their text form, and the original type of each value is lost.")]),
            "hierarchyid" => new TypeMapping("text", [Warn(DiagnosticCodes.HierarchyIdAsText,
                "hierarchyid is kept as its string form (/1/2/); its methods (GetAncestor, IsDescendantOf, …) must be rewritten.")]),
            "geography" or "geometry" => Spatial(type.Name, owner),
            "vector" => Vector(type, owner),
            _ => new TypeMapping("text", [new Diagnostic(DiagnosticCodes.UnknownType, Severity.Error, owner,
                $"No PostgreSQL type is known for SQL Server type '{type}'; it is written as text. Set a type override.")]),
        };
    }

    private TypeMapping Spatial(string name, ObjectName? owner) =>
        options.AllowsExtension(PermittedExtensions.PostGis)
            ? new TypeMapping(name, [ExtensionNote(PermittedExtensions.PostGis, owner, name)], PermittedExtensions.PostGis)
            : new TypeMapping("bytea", [new Diagnostic(DiagnosticCodes.SpatialAsWkb, Severity.Warning, owner,
                $"{name} is kept as well-known binary in bytea because the postgis extension is disabled; spatial functions will not work on it.")]);

    /// <summary>SQL Server 2025's <c>vector(n)</c>; the model carries the dimension count as the length.</summary>
    private TypeMapping Vector(SqlServerType type, ObjectName? owner)
    {
        string dims = type.Length is int n and > 0 ? $"({n.ToString(CultureInfo.InvariantCulture)})" : "";
        return options.AllowsExtension(PermittedExtensions.Vector)
            ? new TypeMapping($"vector{dims}", [ExtensionNote(PermittedExtensions.Vector, owner, "vector")], PermittedExtensions.Vector)
            : new TypeMapping("real[]", [new Diagnostic(DiagnosticCodes.VectorAsArray, Severity.Warning, owner,
                "vector is kept as a real[] array because the vector (pgvector) extension is disabled; distance functions will not work on it.")]);
    }

    private static Diagnostic ExtensionNote(string extension, ObjectName? owner, string what) =>
        new(DiagnosticCodes.ExtensionRequired, Severity.Info, owner, $"{what} needs the {extension} extension.");

    private static TypeMapping Plain(string pgType) => new(pgType, []);

    private static string Length(SqlServerType type, int whenMissing) =>
        (type.Length is int n and > 0 ? n : whenMissing).ToString(CultureInfo.InvariantCulture);

    private static bool IsCaseInsensitive(string collation) => collation.Contains("_CI_", StringComparison.OrdinalIgnoreCase)
        || collation.EndsWith("_CI", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string pgType) =>
        pgType == "text" || pgType.StartsWith("varchar", StringComparison.Ordinal) || pgType.StartsWith("char(", StringComparison.Ordinal);

    private bool TryOverrideIgnoringCase(string key, out string? value)
    {
        foreach (var (k, v) in options.TypeOverrides)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        value = null;
        return false;
    }
}
