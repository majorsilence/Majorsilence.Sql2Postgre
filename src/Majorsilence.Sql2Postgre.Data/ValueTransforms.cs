using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace Majorsilence.Sql2Postgre.Data;

/// <summary>
/// The changes a value goes through between SQL Server and PostgreSQL. Each is applied the same
/// way by the copier and by verification, so the two agree on what a copied value should be.
/// </summary>
public static class ValueTransforms
{
    /// <summary>
    /// SQL Server keeps 100-nanosecond ticks; PostgreSQL keeps microseconds and, given more digits,
    /// rounds half to even (established on PostgreSQL 17: <c>.0000005</c> → <c>.000000</c>,
    /// <c>.0000015</c> → <c>.000002</c>, <c>.0000025</c> → <c>.000002</c>). The copier rounds the same
    /// way before writing, because the binary protocol would otherwise truncate.
    /// </summary>
    public static long RoundTicksToMicroseconds(long ticks)
    {
        long micro = Math.DivRem(ticks, 10, out long rem);
        if (rem > 5 || (rem == 5 && (micro & 1) == 1))
            micro++;
        else if (rem < -5 || (rem == -5 && (micro & 1) == 1))
            micro--;
        return micro * 10;
    }

    /// <summary>
    /// Rounded as PostgreSQL would; the one value that would round past .NET's last tick
    /// (9999-12-31 23:59:59.9999995 and later) is truncated instead, to …59.999999.
    /// </summary>
    public static DateTime RoundToMicroseconds(DateTime value)
    {
        long ticks = RoundTicksToMicroseconds(value.Ticks);
        return new(ticks > DateTime.MaxValue.Ticks ? value.Ticks - value.Ticks % 10 : ticks, value.Kind);
    }

    public static TimeSpan RoundToMicroseconds(TimeSpan value) => new(RoundTicksToMicroseconds(value.Ticks));

    /// <summary>The instant of a <c>datetimeoffset</c>, in UTC, as <c>timestamptz</c> holds it.</summary>
    public static DateTime ToUtc(DateTimeOffset value) =>
        DateTime.SpecifyKind(RoundToMicroseconds(value.UtcDateTime), DateTimeKind.Utc);

    /// <summary>PostgreSQL text cannot contain the NUL character; it is removed, and the count returned.</summary>
    public static string RemoveNul(string value, out int removed)
    {
        int index = value.IndexOf('\0');
        if (index < 0)
        {
            removed = 0;
            return value;
        }
        string cleaned = value.Replace("\0", "");
        removed = value.Length - cleaned.Length;
        return cleaned;
    }

    /// <summary>
    /// PostGIS reads extended WKB: the WKB SQL Server's <c>STAsBinary()</c> writes, with the SRID
    /// flag (0x20000000) set in the geometry type word and the 4-byte SRID after it.
    /// </summary>
    public static byte[] ToEwkb(byte[] wkb, int srid)
    {
        if (wkb.Length < 5)
            throw new FormatException("WKB is shorter than its header.");
        bool little = wkb[0] == 1;
        uint type = little ? BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1)) : BinaryPrimitives.ReadUInt32BigEndian(wkb.AsSpan(1));
        var ewkb = new byte[wkb.Length + 4];
        ewkb[0] = wkb[0];
        uint flagged = type | 0x20000000;
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ewkb.AsSpan(1), flagged);
            BinaryPrimitives.WriteInt32LittleEndian(ewkb.AsSpan(5), srid);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(ewkb.AsSpan(1), flagged);
            BinaryPrimitives.WriteInt32BigEndian(ewkb.AsSpan(5), srid);
        }
        wkb.AsSpan(5).CopyTo(ewkb.AsSpan(9));
        return ewkb;
    }

    /// <summary>A SQL Server <c>vector</c> as its JSON text (<c>[1,2,3]</c>), parsed to floats.</summary>
    public static float[] ParseVector(string json) =>
        JsonSerializer.Deserialize<float[]>(json) ?? throw new FormatException($"'{json}' is not a JSON array of numbers.");

    /// <summary>pgvector's binary form: int16 dimensions, int16 unused, then each element as a big-endian float4.</summary>
    public static byte[] ToPgvectorBinary(float[] values)
    {
        var bytes = new byte[4 + 4 * values.Length];
        BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)values.Length));
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(4 + 4 * i), values[i]);
        return bytes;
    }

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
