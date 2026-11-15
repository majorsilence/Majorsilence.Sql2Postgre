using System.Buffers.Binary;
using Majorsilence.Sql2Postgre.Data;

namespace Majorsilence.Sql2Postgre.Tests.Data;

public class ValueTransformsTests
{
    [TestCase(0L, 0L)]
    [TestCase(4L, 0L)]
    [TestCase(5L, 0L)]   // half, to even (0)
    [TestCase(6L, 10L)]
    [TestCase(15L, 20L)] // half, to even (2)
    [TestCase(25L, 20L)] // half, to even (2)
    [TestCase(35L, 40L)]
    [TestCase(-5L, 0L)]
    [TestCase(-15L, -20L)]
    public void TicksRoundHalfToEven(long ticks, long expected) =>
        Assert.That(ValueTransforms.RoundTicksToMicroseconds(ticks), Is.EqualTo(expected));

    [Test]
    public void LastTickOfTimeIsTruncatedNotOverflowed()
    {
        var last = DateTime.MaxValue; // 9999-12-31 23:59:59.9999999
        Assert.That(ValueTransforms.RoundToMicroseconds(last).Ticks, Is.EqualTo(last.Ticks - 9));
    }

    [Test]
    public void OffsetBecomesUtc()
    {
        var value = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(-5.5));
        var utc = ValueTransforms.ToUtc(value);
        Assert.That(utc, Is.EqualTo(new DateTime(2024, 1, 1, 17, 30, 0, DateTimeKind.Utc)));
        Assert.That(utc.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void NulCharactersAreRemovedAndCounted()
    {
        Assert.That(ValueTransforms.RemoveNul("a\0b\0", out int removed), Is.EqualTo("ab"));
        Assert.That(removed, Is.EqualTo(2));
        Assert.That(ValueTransforms.RemoveNul("plain", out removed), Is.EqualTo("plain"));
        Assert.That(removed, Is.Zero);
    }

    [Test]
    public void EwkbCarriesTheSrid()
    {
        // WKB point (little-endian): byte order 1, type 1, x, y.
        var wkb = new byte[21];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5), 1.5);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13), -2.5);

        var ewkb = ValueTransforms.ToEwkb(wkb, 4326);
        Assert.Multiple(() =>
        {
            Assert.That(ewkb, Has.Length.EqualTo(25));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(ewkb.AsSpan(1)), Is.EqualTo(0x20000001u));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(ewkb.AsSpan(5)), Is.EqualTo(4326));
            Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(ewkb.AsSpan(9)), Is.EqualTo(1.5));
            Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(ewkb.AsSpan(17)), Is.EqualTo(-2.5));
        });
    }

    [Test]
    public void VectorParsesAndEncodesForPgvector()
    {
        var values = ValueTransforms.ParseVector("[1, -2.5, 3e2]");
        Assert.That(values, Is.EqualTo(new[] { 1f, -2.5f, 300f }));
        var bytes = ValueTransforms.ToPgvectorBinary(values);
        Assert.Multiple(() =>
        {
            Assert.That(bytes, Has.Length.EqualTo(16));
            Assert.That(BinaryPrimitives.ReadInt16BigEndian(bytes), Is.EqualTo(3));
            Assert.That(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(2)), Is.Zero);
            Assert.That(BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(8)), Is.EqualTo(-2.5f));
        });
    }
}
