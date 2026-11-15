namespace Majorsilence.Sql2Postgre.Tests.Golden;

/// <summary>
/// One golden test: <c>&lt;case&gt;.tsql</c> is the input, <c>&lt;case&gt;.pgsql</c> the exact
/// expected output and the optional <c>&lt;case&gt;.diag</c> the expected diagnostics, one per
/// line as <c>S2P3001 Warning</c>. A first input line <c>-- options: {json}</c> sets options.
/// </summary>
public sealed record GoldenCase(string Area, string Name, string InputPath)
{
    private const string OptionsPrefix = "-- options:";

    public string ExpectedPath => Path.ChangeExtension(InputPath, ".pgsql");
    public string DiagnosticsPath => Path.ChangeExtension(InputPath, ".diag");

    public override string ToString() => $"{Area}/{Name}";

    /// <summary>The input without its options line, and the options JSON (or null).</summary>
    public (string Sql, string? OptionsJson) ReadInput()
    {
        string text = Normalize(File.ReadAllText(InputPath));
        if (!text.StartsWith(OptionsPrefix, StringComparison.Ordinal))
            return (text, null);
        int end = text.IndexOf('\n');
        string first = end < 0 ? text : text[..end];
        string rest = end < 0 ? "" : text[(end + 1)..];
        return (rest, first[OptionsPrefix.Length..].Trim());
    }

    public string? ReadExpected() => File.Exists(ExpectedPath) ? Normalize(File.ReadAllText(ExpectedPath)) : null;

    public IReadOnlyList<string> ReadExpectedDiagnostics() => File.Exists(DiagnosticsPath)
        ? Normalize(File.ReadAllText(DiagnosticsPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        : [];

    /// <summary>Line endings to <c>\n</c>, trailing whitespace trimmed from every line, one final newline.</summary>
    public static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    public static IEnumerable<GoldenCase> Discover()
    {
        string root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden");
        if (!Directory.Exists(root))
            yield break;
        foreach (string file in Directory.EnumerateFiles(root, "*.tsql", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            string area = Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/');
            yield return new GoldenCase(area, Path.GetFileNameWithoutExtension(file), file);
        }
    }

    /// <summary>The source-tree copy of a file, so <c>SQL2PG_UPDATE_GOLDEN=1</c> rewrites what is committed.</summary>
    public static string SourcePath(string outputPath)
    {
        string relative = Path.GetRelativePath(TestContext.CurrentContext.TestDirectory, outputPath);
        string? dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Majorsilence.Sql2Postgre.Tests.csproj")))
            dir = Path.GetDirectoryName(dir);
        return dir is null ? outputPath : Path.Combine(dir, relative);
    }
}
