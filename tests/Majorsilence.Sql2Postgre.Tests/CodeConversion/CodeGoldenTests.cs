using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Tests.CodeConversion;

/// <summary>
/// Golden files for <c>sql2pg code</c>: <c>CodeConversion/Golden/&lt;area&gt;/&lt;name&gt;.cs.in</c> (or
/// <c>.vb.in</c>) converts to <c>&lt;name&gt;.cs.out</c>, with its diagnostics in <c>&lt;name&gt;.cs.diag</c>
/// (<c>code severity line:column</c>). A file whose name contains <c>-schema</c> is converted with the
/// shop fixture as <c>--source</c>. With <c>SQL2PG_UPDATE_GOLDEN=1</c> the expected files are rewritten.
/// </summary>
public class CodeGoldenTests
{
    private static string Directory => Path.Combine(TestContext.CurrentContext.TestDirectory, "CodeConversion", "Golden");

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Golden");

    private static bool Update => Environment.GetEnvironmentVariable("SQL2PG_UPDATE_GOLDEN") == "1";

    public static IEnumerable<TestCaseData> Cases() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.in", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => new TestCaseData(Path.GetRelativePath(Directory, f).Replace('\\', '/')).SetName($"CodeGolden({Path.GetRelativePath(Directory, f).Replace('\\', '/')})"))
            : [];

    private static readonly Lazy<SemanticModel> Shop = new(() => SemanticModel.Build(SqlServerScriptReader.Read([("shop.sql",
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")))]).Database));

    [TestCaseSource(nameof(Cases))]
    public void Matches(string relative)
    {
        string input = File.ReadAllText(Path.Combine(Directory, relative)).Replace("\r\n", "\n");
        string fileName = Path.GetFileNameWithoutExtension(relative); // name.cs
        var options = new CodeConversionOptions(new ConversionOptions(), fileName.Contains("-schema", StringComparison.Ordinal) ? Shop.Value : null);
        var result = new CodeConverter(options).ConvertFile(fileName, input);
        string diag = string.Concat(result.Diagnostics.Select(d => $"{d.Code} {d.Severity} {d.Span?.Line}:{d.Span?.Column}\n"));

        string stem = relative[..^".in".Length];
        string outRelative = stem + ".out";
        string diagRelative = stem + ".diag";
        if (Update)
        {
            File.WriteAllText(Path.Combine(SourceDirectory(), outRelative), result.NewText);
            string diagPath = Path.Combine(SourceDirectory(), diagRelative);
            if (diag.Length > 0)
                File.WriteAllText(diagPath, diag);
            else if (File.Exists(diagPath))
                File.Delete(diagPath);
            Assert.Pass("Expected files rewritten (SQL2PG_UPDATE_GOLDEN=1).");
        }
        string expectedOut = Path.Combine(Directory, outRelative);
        Assert.That(File.Exists(expectedOut), Is.True, $"Missing {outRelative}; run with SQL2PG_UPDATE_GOLDEN=1 to create it.");
        string expectedDiag = Path.Combine(Directory, diagRelative);
        Assert.Multiple(() =>
        {
            Assert.That(result.NewText, Is.EqualTo(File.ReadAllText(expectedOut).Replace("\r\n", "\n")), "Converted code");
            Assert.That(diag, Is.EqualTo(File.Exists(expectedDiag) ? File.ReadAllText(expectedDiag).Replace("\r\n", "\n") : ""), "Diagnostics");
        });
    }
}
