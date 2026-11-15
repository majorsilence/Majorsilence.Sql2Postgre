namespace Majorsilence.Sql2Postgre.Tests.Golden;

/// <summary>
/// Runs every case under <c>Golden/</c> through <see cref="GoldenTranslator"/> and compares the
/// normalized output and diagnostics with the expected files. With the environment variable
/// <c>SQL2PG_UPDATE_GOLDEN=1</c> it rewrites the expected files in the source tree instead of
/// failing, for a reviewer to read in the diff. Never set it in CI.
/// </summary>
public class GoldenTests
{
    private static bool Update => Environment.GetEnvironmentVariable("SQL2PG_UPDATE_GOLDEN") == "1";

    public static IEnumerable<TestCaseData> Cases() =>
        GoldenCase.Discover().Select(c => new TestCaseData(c).SetName($"Golden({c})"));

    [TestCaseSource(nameof(Cases))]
    public void Matches(GoldenCase golden)
    {
        var (sql, optionsJson) = golden.ReadInput();
        var result = GoldenTranslator.Translate(sql, optionsJson);
        string actual = GoldenCase.Normalize(result.Sql);
        var actualDiagnostics = result.Diagnostics.Select(d => $"{d.Code} {d.Severity}").Order(StringComparer.Ordinal).ToList();

        if (Update)
        {
            File.WriteAllText(GoldenCase.SourcePath(golden.ExpectedPath), actual);
            string diagPath = GoldenCase.SourcePath(golden.DiagnosticsPath);
            if (actualDiagnostics.Count > 0)
                File.WriteAllText(diagPath, string.Join('\n', actualDiagnostics) + "\n");
            else if (File.Exists(diagPath))
                File.Delete(diagPath);
            Assert.Pass("Expected files rewritten (SQL2PG_UPDATE_GOLDEN=1).");
        }

        string? expected = golden.ReadExpected();
        Assert.That(expected, Is.Not.Null, $"Missing {Path.GetFileName(golden.ExpectedPath)}; run with SQL2PG_UPDATE_GOLDEN=1 to create it.");
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected), "Translated SQL");
            Assert.That(actualDiagnostics, Is.EqualTo(golden.ReadExpectedDiagnostics().Order(StringComparer.Ordinal).ToList()), "Diagnostics");
        });
    }

    [Test]
    public void NormalizeTrimsAndUnifiesLineEndings() =>
        Assert.That(GoldenCase.Normalize("a  \r\nb\t\r\n\r\n"), Is.EqualTo("a\nb\n"));
}
