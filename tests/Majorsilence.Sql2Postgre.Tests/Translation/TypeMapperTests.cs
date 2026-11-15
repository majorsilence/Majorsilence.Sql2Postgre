using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Types;

namespace Majorsilence.Sql2Postgre.Tests.Translation;

public class TypeMapperTests
{
    private static readonly ObjectName T = new("dbo", "t");

    private static TypeMapper Mapper(ConversionOptions? options = null)
    {
        options ??= new ConversionOptions();
        return new TypeMapper(options, new NameMapper(options));
    }

    private static TypeMapping Map(SqlServerType type, ConversionOptions? options = null) => Mapper(options).Map(type, T, "c");

    /// <summary>Every row of the plan's type table that needs no diagnostic.</summary>
    [TestCase("bit", null, null, null, "boolean")]
    [TestCase("tinyint", null, null, null, "smallint")]
    [TestCase("smallint", null, null, null, "smallint")]
    [TestCase("int", null, null, null, "integer")]
    [TestCase("bigint", null, null, null, "bigint")]
    [TestCase("decimal", null, 18, 4, "numeric(18,4)")]
    [TestCase("numeric", null, 10, 2, "numeric(10,2)")]
    [TestCase("money", null, null, null, "numeric(19,4)")]
    [TestCase("smallmoney", null, null, null, "numeric(10,4)")]
    [TestCase("real", null, 24, null, "real")]
    [TestCase("float", null, 24, null, "real")]
    [TestCase("float", null, 53, null, "double precision")]
    [TestCase("char", 10, null, null, "char(10)")]
    [TestCase("nchar", 10, null, null, "char(10)")]
    [TestCase("varchar", 50, null, null, "varchar(50)")]
    [TestCase("nvarchar", 50, null, null, "varchar(50)")]
    [TestCase("varchar", -1, null, null, "text")]
    [TestCase("nvarchar", -1, null, null, "text")]
    [TestCase("text", null, null, null, "text")]
    [TestCase("ntext", null, null, null, "text")]
    [TestCase("binary", 8, null, null, "bytea")]
    [TestCase("varbinary", 16, null, null, "bytea")]
    [TestCase("varbinary", -1, null, null, "bytea")]
    [TestCase("image", null, null, null, "bytea")]
    [TestCase("date", null, null, null, "date")]
    [TestCase("time", null, null, 3, "time(3)")]
    [TestCase("datetime", null, null, null, "timestamp(3)")]
    [TestCase("smalldatetime", null, null, null, "timestamp(0)")]
    [TestCase("datetime2", null, null, 3, "timestamp(3)")]
    [TestCase("datetime2", null, null, 0, "timestamp(0)")]
    [TestCase("uniqueidentifier", null, null, null, "uuid")]
    [TestCase("xml", null, null, null, "xml")]
    [TestCase("sysname", null, null, null, "varchar(128)")]
    public void PlainMappings(string name, int? length, int? precision, int? scale, string expected)
    {
        var m = Map(new SqlServerType(name, length, precision, scale));
        Assert.Multiple(() =>
        {
            Assert.That(m.PgType, Is.EqualTo(expected));
            Assert.That(m.Diagnostics, Is.Empty);
            Assert.That(m.Extension, Is.Null);
        });
    }

    [TestCase("time", 7, "time(6)")]
    [TestCase("datetime2", 7, "timestamp(6)")]
    public void SevenFractionalDigitsBecomeSix(string name, int scale, string expected)
    {
        var m = Map(new SqlServerType(name, Scale: scale));
        Assert.That(m.PgType, Is.EqualTo(expected));
        Assert.That(m.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.FractionalSecondsReduced));
    }

    [Test]
    public void DatetimeoffsetLosesItsOffset()
    {
        var m = Map(new SqlServerType("datetimeoffset", Scale: 3));
        Assert.That(m.PgType, Is.EqualTo("timestamptz(3)"));
        Assert.That(m.Diagnostics.Select(d => (d.Code, d.Severity)), Is.EqualTo(new[] { (DiagnosticCodes.OffsetNotPreserved, Severity.Warning) }));
        Assert.That(Map(new SqlServerType("datetimeoffset", Scale: 7)).Diagnostics.Select(d => d.Code),
            Is.EquivalentTo(new[] { DiagnosticCodes.OffsetNotPreserved, DiagnosticCodes.FractionalSecondsReduced }));
    }

    [TestCase("timestamp", "bytea", DiagnosticCodes.RowversionSemantics)]
    [TestCase("sql_variant", "text", DiagnosticCodes.SqlVariantAsText)]
    [TestCase("hierarchyid", "text", DiagnosticCodes.HierarchyIdAsText)]
    public void LossyMappingsWarn(string name, string expected, string code)
    {
        var m = Map(new SqlServerType(name));
        Assert.That(m.PgType, Is.EqualTo(expected));
        Assert.That(m.Diagnostics.Single(), Has.Property("Code").EqualTo(code).And.Property("Severity").EqualTo(Severity.Warning));
    }

    [TestCase("geography")]
    [TestCase("geometry")]
    public void SpatialUsesPostGisWhenEnabled(string name)
    {
        var m = Map(new SqlServerType(name));
        Assert.Multiple(() =>
        {
            Assert.That(m.PgType, Is.EqualTo(name));
            Assert.That(m.Extension, Is.EqualTo("postgis"));
            Assert.That(m.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.ExtensionRequired));
        });
    }

    [Test]
    public void SpatialFallsBackToWkbWithoutPostGis()
    {
        var m = Map(new SqlServerType("geography"), new ConversionOptions { Extensions = new HashSet<string>() });
        Assert.Multiple(() =>
        {
            Assert.That(m.PgType, Is.EqualTo("bytea"));
            Assert.That(m.Extension, Is.Null);
            Assert.That(m.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.SpatialAsWkb));
        });
    }

    [Test]
    public void VectorUsesPgvectorOrFallsBackToAnArray()
    {
        var with = Map(new SqlServerType("vector", 3));
        var without = Map(new SqlServerType("vector", 3), new ConversionOptions { Extensions = new HashSet<string> { "postgis" } });
        Assert.Multiple(() =>
        {
            Assert.That(with.PgType, Is.EqualTo("vector(3)"));
            Assert.That(with.Extension, Is.EqualTo("vector"));
            Assert.That(without.PgType, Is.EqualTo("real[]"));
            Assert.That(without.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.VectorAsArray));
        });
    }

    [Test]
    public void AliasTypeUsesItsDomain() =>
        Assert.That(Map(new SqlServerType("nvarchar", 20, UserTypeName: "shop.Phone")).PgType, Is.EqualTo("shop.phone"));

    [Test]
    public void UnknownTypeIsAnError()
    {
        var m = Map(new SqlServerType("cursor"));
        Assert.That(m.PgType, Is.EqualTo("text"));
        Assert.That(m.Diagnostics.Single(), Has.Property("Code").EqualTo(DiagnosticCodes.UnknownType).And.Property("Severity").EqualTo(Severity.Error));
    }

    [Test]
    public void OverrideWinsAndMatchesCaseInsensitively()
    {
        var options = new ConversionOptions { TypeOverrides = new Dictionary<string, string> { ["DBO.T.C"] = "jsonb" } };
        Assert.That(Mapper(options).Map(new SqlServerType("nvarchar", -1), T, "c").PgType, Is.EqualTo("jsonb"));
    }

    [Test]
    public void CaseInsensitiveCollationIsReported()
    {
        var m = Map(new SqlServerType("nvarchar", 50, Collation: "SQL_Latin1_General_CP1_CI_AS"));
        Assert.That(m.Diagnostics.Single(), Has.Property("Code").EqualTo(DiagnosticCodes.CaseInsensitiveCollation).And.Property("Severity").EqualTo(Severity.Info));
        Assert.That(Map(new SqlServerType("nvarchar", 50, Collation: "Latin1_General_CS_AS")).Diagnostics, Is.Empty);
        Assert.That(Map(new SqlServerType("nvarchar", 50, Collation: "Latin1_General_CI_AS"),
            new ConversionOptions { CaseInsensitive = CaseInsensitiveStrategy.IcuCollation }).Diagnostics, Is.Empty);
    }
}
