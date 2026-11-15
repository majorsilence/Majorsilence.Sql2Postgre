using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Tests.Postgres;

public partial class SchemaConversionTests
{
    private static SourceDatabase Shop() => SqlServerScriptReader.Read([("shop.sql",
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")))]).Database;

    private static string AllScripts(ConversionResult r) =>
        string.Concat(Enum.GetValues<Phase>().Select(p => ScriptPlanner.PhaseScript(r, p))) + ScriptPlanner.Report(r);

    [Test]
    public void OutputIsDeterministic()
    {
        var first = AllScripts(SchemaConverter.Convert(Shop(), new ConversionOptions()));
        var second = AllScripts(SchemaConverter.Convert(Shop(), new ConversionOptions()));
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void EveryGoldenExtensionIsPermitted()
    {
        string root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden");
        var used = Directory.EnumerateFiles(root, "*.pgsql", SearchOption.AllDirectories)
            .SelectMany(f => CreateExtension().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value.Trim('"')))
            .Distinct().ToList();
        Assert.That(used, Is.SubsetOf(PermittedExtensions.All));
    }

    [Test]
    public void NoExtensionWhenNoneAreEnabled()
    {
        var result = SchemaConverter.Convert(Shop(), new ConversionOptions { Extensions = new HashSet<string>() });
        Assert.That(CreateExtension().IsMatch(AllScripts(result)), Is.False);
    }

    [Test]
    public void ShopNeedsOnlyPostGis()
    {
        var result = SchemaConverter.Convert(Shop(), new ConversionOptions());
        string scripts = string.Concat(Enum.GetValues<Phase>().Select(p => ScriptPlanner.PhaseScript(result, p)));
        Assert.That(CreateExtension().Matches(scripts).Select(m => m.Groups[1].Value), Is.EqualTo(new[] { "postgis" }));
    }

    [Test]
    public void NoInternalErrorsOnTheFixture()
    {
        var result = SchemaConverter.Convert(Shop(), new ConversionOptions());
        Assert.That(result.Objects.SelectMany(o => o.Diagnostics).Where(d => d.Code == DiagnosticCodes.InternalError), Is.Empty);
    }

    [Test]
    public void PhasesAreInOrder()
    {
        var phases = SchemaConverter.Convert(Shop(), new ConversionOptions()).Objects.Select(o => o.Phase).ToList();
        Assert.That(phases, Is.Ordered);
    }

    [GeneratedRegex(@"CREATE EXTENSION IF NOT EXISTS ([^;\s]+);")]
    private static partial Regex CreateExtension();
}

public class ScriptPlannerTests
{
    private static ConvertedObject O(string name, Phase phase, params string[] dependsOn) =>
        new(new ObjectName("dbo", name), ObjectKind.Table, $"public.{name}", phase, $"-- {name}\n", ObjectStatus.Converted, [],
            dependsOn.Select(d => new ObjectName("dbo", d)).ToList());

    [Test]
    public void DependenciesComeFirstWithinAPhaseThenByName()
    {
        var (ordered, diagnostics) = ScriptPlanner.Order([O("c", Phase.Code), O("a", Phase.Code, "c"), O("b", Phase.Code), O("z", Phase.Tables)]);
        Assert.Multiple(() =>
        {
            Assert.That(ordered.Select(o => o.Source.Name), Is.EqualTo(new[] { "z", "b", "c", "a" }));
            Assert.That(diagnostics, Is.Empty);
        });
    }

    [Test]
    public void DependenciesInOtherPhasesAreIgnored()
    {
        var (ordered, _) = ScriptPlanner.Order([O("b", Phase.Tables, "a"), O("a", Phase.Code)]);
        Assert.That(ordered.Select(o => o.Source.Name), Is.EqualTo(new[] { "b", "a" }));
    }

    [Test]
    public void CycleIsEmittedInNameOrderAndReported()
    {
        var (ordered, diagnostics) = ScriptPlanner.Order([O("b", Phase.Code, "a"), O("a", Phase.Code, "b"), O("x", Phase.Code)]);
        Assert.Multiple(() =>
        {
            Assert.That(ordered.Select(o => o.Source.Name), Is.EqualTo(new[] { "x", "a", "b" }));
            Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.DependencyCycle));
        });
    }

    [Test]
    public void FilesRoundTripThroughTheirMarkers()
    {
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "planner-" + Guid.NewGuid().ToString("N"));
        var result = new ConversionResult([O("t", Phase.Tables), O("f", Phase.Code)], []);
        ScriptPlanner.WriteFiles(result, dir);
        var units = ScriptPlanner.ReadScripts(dir);
        Assert.Multiple(() =>
        {
            Assert.That(Directory.GetFiles(dir, "*.sql"), Has.Length.EqualTo(Enum.GetValues<Phase>().Length));
            Assert.That(File.Exists(Path.Combine(dir, ScriptPlanner.ReportFile)), Is.True);
            Assert.That(units.Select(u => u.TargetName), Is.EqualTo(new[] { "public.t", "public.f" }));
            Assert.That(units[0].Sql, Is.EqualTo("-- t\n"));
            Assert.That(units[1].Sql, Is.EqualTo("SET check_function_bodies = off;\n-- f\n"), "code units carry the file's setup");
            Assert.That(units[1].File, Is.EqualTo("06-code.sql"));
        });
        Directory.Delete(dir, recursive: true);
    }
}
