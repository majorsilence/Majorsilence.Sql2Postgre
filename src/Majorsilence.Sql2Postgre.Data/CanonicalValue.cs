using System.Globalization;
using System.Numerics;

namespace Majorsilence.Sql2Postgre.Data;

/// <summary>
/// Writes a value read from SQL Server or PostgreSQL as one canonical string, so the two engines'
/// values can be compared: by the both-engines tests, and by data verification. The two drivers
/// hand back different CLR types for the same SQL value (a SQL Server <c>date</c> arrives as a
/// <see cref="DateTime"/>, a PostgreSQL <c>timestamptz</c> as a UTC <see cref="DateTime"/>), so
/// formatting is by value and, where the CLR type alone is ambiguous, by the column's type name.
/// </summary>
public static class CanonicalValue
{
    /// <summary>The canonical form of SQL <c>NULL</c>.</summary>
    public const string Null = "\\N";

    /// <param name="value">The value as the driver returned it (<see cref="DBNull"/> is NULL).</param>
    /// <param name="typeName">The column's type name as the driver reports it (<c>GetDataTypeName</c>), when known.</param>
    public static string Format(object? value, string? typeName = null) => value switch
    {
        null or DBNull => Null,
        bool b => b ? "1" : "0",
        string s => s,
        char c => c.ToString(),
        sbyte or byte or short or ushort or int or uint or long or ulong or BigInteger =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        decimal d => FormatDecimal(d),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt when IsDateOnlyType(typeName) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => FormatTimestamp(dt, dt.Kind == DateTimeKind.Utc),
        DateTimeOffset dto => FormatTimestamp(dto.UtcDateTime, utc: true),
        TimeOnly t => FormatTime(t.ToTimeSpan()),
        TimeSpan ts => FormatTime(ts),
        Guid g => g.ToString("D"),
        byte[] bytes => Convert.ToHexStringLower(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? Null,
    };

    /// <summary>Invariant, no trailing zeros after the point, and negative zero as <c>0</c>.</summary>
    private static string FormatDecimal(decimal d)
    {
        string s = d.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.'))
            s = s.TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }

    /// <summary>Microsecond precision (PostgreSQL's), truncated; <c>Z</c> marks a UTC instant.</summary>
    private static string FormatTimestamp(DateTime dt, bool utc)
    {
        long micro = dt.Ticks % TimeSpan.TicksPerSecond / 10;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{dt:yyyy-MM-dd}T{dt:HH:mm:ss}.{micro:000000}");
        return utc ? text + "Z" : text;
    }

    private static string FormatTime(TimeSpan ts)
    {
        long micro = ts.Ticks % TimeSpan.TicksPerSecond / 10;
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{micro:000000}");
    }

    private static bool IsDateOnlyType(string? typeName) =>
        string.Equals(typeName, "date", StringComparison.OrdinalIgnoreCase);
}
