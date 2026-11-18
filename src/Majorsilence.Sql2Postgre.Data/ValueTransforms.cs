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
    /// flag (0x20000000) set in the geometry type word and the 4-byte SRID after it. SRID 0 is
    /// PostGIS's "unknown", which it stores and writes without the flag, so that WKB stays as it is.
    /// </summary>
    public static byte[] ToEwkb(byte[] wkb, int srid)
    {
        if (wkb.Length < 5)
            throw new FormatException("WKB is shorter than its header.");
        if (srid == 0)
            return wkb;
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

    /// <summary>How far past ±180 longitude or ±90 latitude PostGIS geography takes a value as the limit.</summary>
    public const double GeodeticNudge = 1e-10;

    /// <summary>
    /// A geography value's (E)WKB with every longitude and latitude a hair outside its range (at most
    /// <see cref="GeodeticNudge"/>) set to the limit, as PostGIS stores it. SQL Server keeps such
    /// values (a border at 180.00000000000003), and without this the copy would verify as different.
    /// The input is not changed.
    /// </summary>
    public static byte[] NudgeGeodetic(byte[] wkb)
    {
        var copy = (byte[])wkb.Clone();
        int offset = 0;
        NudgeGeometry(copy, ref offset);
        return copy;
    }

    private static void NudgeGeometry(byte[] b, ref int at)
    {
        bool little = b[at] == 1;
        uint type = little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 1)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 1));
        at += 5;
        if ((type & 0x20000000) != 0)
            at += 4; // the SRID
        uint iso = type & 0x0FFFFFFF;
        int dims = 2 + ((type & 0x80000000) != 0 || iso % 10000 / 1000 is 1 or 3 ? 1 : 0) + ((type & 0x40000000) != 0 || iso % 10000 / 1000 is 2 or 3 ? 1 : 0);
        uint kind = iso % 1000;
        uint Count(ref int p)
        {
            uint n = little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            p += 4;
            return n;
        }
        void Points(ref int p, uint n)
        {
            for (uint i = 0; i < n; i++, p += 8 * dims)
            {
                Nudge(b, p, 180, little);
                Nudge(b, p + 8, 90, little);
            }
        }
        switch (kind)
        {
            case 1:
                Points(ref at, 1);
                break;
            case 2:
                Points(ref at, Count(ref at));
                break;
            case 3:
                for (uint rings = Count(ref at), r = 0; r < rings; r++)
                    Points(ref at, Count(ref at));
                break;
            case 4 or 5 or 6 or 7:
                for (uint parts = Count(ref at), g = 0; g < parts; g++)
                    NudgeGeometry(b, ref at);
                break;
            default:
                throw new FormatException($"WKB geometry type {kind} is not one SQL Server writes.");
        }
    }

    private static void Nudge(byte[] b, int at, double limit, bool little)
    {
        var span = b.AsSpan(at, 8);
        double v = little ? BinaryPrimitives.ReadDoubleLittleEndian(span) : BinaryPrimitives.ReadDoubleBigEndian(span);
        double fixedValue = v > limit && v - limit <= GeodeticNudge ? limit : v < -limit && -limit - v <= GeodeticNudge ? -limit : v;
        if (fixedValue == v)
            return;
        if (little)
            BinaryPrimitives.WriteDoubleLittleEndian(span, fixedValue);
        else
            BinaryPrimitives.WriteDoubleBigEndian(span, fixedValue);
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
