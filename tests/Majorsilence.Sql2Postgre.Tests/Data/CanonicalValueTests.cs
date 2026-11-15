using System.Numerics;
using Majorsilence.Sql2Postgre.Data;

namespace Majorsilence.Sql2Postgre.Tests.Data;

public class CanonicalValueTests
{
    [Test]
    public void NullAndDbNullAreBackslashN()
    {
        Assert.That(CanonicalValue.Format(null), Is.EqualTo("\\N"));
        Assert.That(CanonicalValue.Format(DBNull.Value), Is.EqualTo("\\N"));
    }

    [TestCase(true, "1")]
    [TestCase(false, "0")]
    public void BooleansAreOneAndZero(bool value, string expected) =>
        Assert.That(CanonicalValue.Format(value), Is.EqualTo(expected));

    [Test]
    public void IntegersAreInvariantDecimal()
    {
        Assert.That(CanonicalValue.Format((byte)255), Is.EqualTo("255"));
        Assert.That(CanonicalValue.Format((short)-32768), Is.EqualTo("-32768"));
        Assert.That(CanonicalValue.Format(int.MinValue), Is.EqualTo("-2147483648"));
        Assert.That(CanonicalValue.Format(long.MaxValue), Is.EqualTo("9223372036854775807"));
        Assert.That(CanonicalValue.Format(BigInteger.Parse("123456789012345678901234567890")), Is.EqualTo("123456789012345678901234567890"));
    }

    [TestCase("1.2300", "1.23")]
    [TestCase("10.000", "10")]
    [TestCase("100", "100")]
    [TestCase("-0.00", "0")]
    [TestCase("-12.50", "-12.5")]
    [TestCase("0.0001", "0.0001")]
    public void DecimalsDropTrailingZerosAndNegativeZero(string input, string expected) =>
        Assert.That(CanonicalValue.Format(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)), Is.EqualTo(expected));

    [Test]
    public void FloatsRoundTrip()
    {
        Assert.That(CanonicalValue.Format(0.1d), Is.EqualTo("0.1"));
        Assert.That(CanonicalValue.Format(1.0 / 3.0), Is.EqualTo((1.0 / 3.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(CanonicalValue.Format(1.5f), Is.EqualTo("1.5"));
    }

    [Test]
    public void DatesFromEitherClrType()
    {
        Assert.That(CanonicalValue.Format(new DateOnly(2024, 2, 29)), Is.EqualTo("2024-02-29"));
        Assert.That(CanonicalValue.Format(new DateTime(2024, 2, 29), "date"), Is.EqualTo("2024-02-29"));
        Assert.That(CanonicalValue.Format(new DateTime(2024, 2, 29), "DATE"), Is.EqualTo("2024-02-29"));
    }

    [Test]
    public void TimestampsHaveSixFractionalDigitsTruncated()
    {
        var dt = new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(1234567);
        Assert.That(CanonicalValue.Format(dt), Is.EqualTo("2024-01-02T03:04:05.123456"));
        Assert.That(CanonicalValue.Format(new DateTime(2024, 1, 2)), Is.EqualTo("2024-01-02T00:00:00.000000"));
    }

    [Test]
    public void UtcInstantsEndInZ()
    {
        var offset = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5));
        Assert.That(CanonicalValue.Format(offset), Is.EqualTo("2024-01-02T08:04:05.000000Z"));
        var utc = new DateTime(2024, 1, 2, 8, 4, 5, DateTimeKind.Utc);
        Assert.That(CanonicalValue.Format(utc), Is.EqualTo("2024-01-02T08:04:05.000000Z"));
    }

    [Test]
    public void TimesFromEitherClrType()
    {
        Assert.That(CanonicalValue.Format(new TimeSpan(0, 23, 59, 59, 999).Add(TimeSpan.FromTicks(9))), Is.EqualTo("23:59:59.999000"));
        Assert.That(CanonicalValue.Format(new TimeOnly(1, 2, 3)), Is.EqualTo("01:02:03.000000"));
    }

    [Test]
    public void GuidsAreLowerCaseD()
    {
        var g = Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF");
        Assert.That(CanonicalValue.Format(g), Is.EqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
    }

    [Test]
    public void BinaryIsLowerCaseHex() =>
        Assert.That(CanonicalValue.Format(new byte[] { 0x00, 0xAB, 0xFF }), Is.EqualTo("00abff"));

    [Test]
    public void StringsAreAsIs()
    {
        Assert.That(CanonicalValue.Format("  a b  "), Is.EqualTo("  a b  "));
        Assert.That(CanonicalValue.Format(""), Is.EqualTo(""));
        Assert.That(CanonicalValue.Format('x'), Is.EqualTo("x"));
    }
}
