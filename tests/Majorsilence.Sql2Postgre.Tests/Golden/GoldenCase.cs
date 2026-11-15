namespace Majorsilence.Sql2Postgre.Tests.Golden;

/// <summary>
/// One golden test: <c>&lt;case&gt;.tsql</c> is the input, <c>&lt;case&gt;.pgsql</c> the exact
/// expected output and the optional <c>&lt;case&gt;.diag</c> the expected diagnostics, one per
/// line as <c>S2P3001 Warning</c>. Leading <c>-- name: value</c> lines are headers:
/// <c>-- options: {json}</c> sets <c>ConversionOptions</c>; an area may define others.
/// </summary>
public sealed record GoldenCase(string Area, string Name, string InputPath)
{
    public string ExpectedPath => Path.ChangeExtension(InputPath, ".pgsql");
    public string DiagnosticsPath => Path.ChangeExtension(InputPath, ".diag");

    public override string ToString() => $"{Area}/{Name}";

    /// <summary>The input without its header lines, and the headers by (lower-case) name.</summary>
    public (string Sql, IReadOnlyDictionary<string, string> Headers) ReadInput()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = Normalize(File.ReadAllText(InputPath)).Split('\n').ToList();
        while (lines.Count > 0 && System.Text.RegularExpressions.Regex.Match(lines[0], @"^--\s*([A-Za-z-]+):\s*(.*)$") is { Success: true } m)
        {
            headers[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            lines.RemoveAt(0);
        }
        return (string.Join('\n', lines), headers);
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
