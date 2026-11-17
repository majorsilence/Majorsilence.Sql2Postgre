using System.Reflection;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Tests.Model;

/// <summary>Every diagnostic code is documented, and the effort weights follow their rule.</summary>
public class DiagnosticDocumentationTests
{
    private static IEnumerable<(string Name, string Code)> Codes() =>
        typeof(DiagnosticCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!));

    private static string Docs()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "docs", "diagnostics.md");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("docs/diagnostics.md not found above the test directory.");
    }

    [Test]
    public void EveryCodeHasASectionUnderItsName()
    {
        string docs = Docs();
        var missing = Codes().Where(c => !docs.Contains($"### {c.Code} {c.Name} (", StringComparison.Ordinal)).Select(c => $"{c.Code} {c.Name}").ToList();
        Assert.That(missing, Is.Empty, "add a '### <code> <Name> (<Severity>)' section to docs/diagnostics.md");
    }

    [Test]
    public void CodesAreUnique() =>
        Assert.That(Codes().GroupBy(c => c.Code).Where(g => g.Count() > 1).Select(g => g.Key), Is.Empty);

    [Test]
    public void EffortWeighsBySeverityWithOverrides()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DiagnosticCodes.Weight(DiagnosticCodes.HintDropped, Severity.Info), Is.Zero);
            Assert.That(DiagnosticCodes.Weight(DiagnosticCodes.SelectAssignmentOrder, Severity.Warning), Is.EqualTo(1));
            Assert.That(DiagnosticCodes.Weight(DiagnosticCodes.ProceduralStatement, Severity.Error), Is.EqualTo(5));
            Assert.That(DiagnosticCodes.Weight(DiagnosticCodes.EncryptedModule, Severity.Error), Is.EqualTo(20));
            Assert.That(DiagnosticCodes.Weight(DiagnosticCodes.DependsOnUnconverted, Severity.Error), Is.Zero, "counted on the object it waits for");
            Assert.That(DiagnosticCodes.Effort(
            [
                new Diagnostic(DiagnosticCodes.ProceduralStatement, Severity.Error, null, "a"),
                new Diagnostic(DiagnosticCodes.SelectAssignmentOrder, Severity.Warning, null, "b"),
            ]), Is.EqualTo(6));
        });
    }
}
