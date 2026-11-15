using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Tests.Model;

public class ConversionOptionsTests
{
    [Test]
    public void DefaultsRoundTripThroughJson()
    {
        var options = new ConversionOptions();
        var back = ConversionOptions.FromJson(options.ToJson());
        Assert.That(back.ToJson(), Is.EqualTo(options.ToJson()));
    }

    [Test]
    public void EveryPropertyRoundTripsThroughJson()
    {
        var options = new ConversionOptions
        {
            TargetVersion = 15,
            NameStyle = NameStyle.SnakeCase,
            CaseInsensitive = CaseInsensitiveStrategy.IcuCollation,
            ResultSetStyle = ResultSetStyle.Refcursor,
            UseCompatSchema = false,
            CompatSchemaName = "helpers",
            SchemaMap = new Dictionary<string, string> { ["dbo"] = "app", ["sales"] = "sales" },
            TypeOverrides = new Dictionary<string, string> { ["dbo.t.c"] = "text" },
            ExcludeObjects = ["staging.*"],
            Extensions = new HashSet<string> { "postgis" },
        };
        string json = options.ToJson();
        var back = ConversionOptions.FromJson(json);

        Assert.Multiple(() =>
        {
            Assert.That(back.ToJson(), Is.EqualTo(json));
            Assert.That(back.NameStyle, Is.EqualTo(NameStyle.SnakeCase));
            Assert.That(back.Extensions, Is.EquivalentTo(new[] { "postgis" }));
            Assert.That(back.SchemaMap["sales"], Is.EqualTo("sales"));
            Assert.That(json, Does.Contain("\"nameStyle\": \"snakeCase\""));
        });
    }

    [Test]
    public void PartialJsonKeepsDefaults()
    {
        var options = ConversionOptions.FromJson("""{ "targetVersion": 18 }""");
        Assert.Multiple(() =>
        {
            Assert.That(options.TargetVersion, Is.EqualTo(18));
            Assert.That(options.SchemaMap["dbo"], Is.EqualTo("public"));
            Assert.That(options.Extensions, Is.EquivalentTo(PermittedExtensions.All));
        });
    }

    [Test]
    public void DefaultsAreValid() => Assert.That(new ConversionOptions().Validate(), Is.Empty);

    [Test]
    public void UnpermittedExtensionAndBadVersionAreRejected()
    {
        var errors = new ConversionOptions { TargetVersion = 14, Extensions = new HashSet<string> { "citext", "postgis" } }.Validate();
        Assert.That(errors, Has.Count.EqualTo(2));
        Assert.That(errors, Has.Some.Contains("citext"));
    }

    [Test]
    public void PermittedListIsTheMaintainersList() =>
        Assert.That(PermittedExtensions.All, Is.EquivalentTo(new[]
            { "hstore", "pg_cron", "pg_stat_statements", "pg_trgm", "pgcrypto", "postgis", "uuid-ossp", "vector" }));

    [Test]
    public void StatusFollowsWorstSeverity()
    {
        Diagnostic D(Severity s) => new("S2P9001", s, null, "m");
        Assert.Multiple(() =>
        {
            Assert.That(ConvertedObject.StatusFor([]), Is.EqualTo(ObjectStatus.Converted));
            Assert.That(ConvertedObject.StatusFor([D(Severity.Info)]), Is.EqualTo(ObjectStatus.Converted));
            Assert.That(ConvertedObject.StatusFor([D(Severity.Info), D(Severity.Warning)]), Is.EqualTo(ObjectStatus.ConvertedWithWarnings));
            Assert.That(ConvertedObject.StatusFor([D(Severity.Warning), D(Severity.Error)]), Is.EqualTo(ObjectStatus.Manual));
        });
    }
}
