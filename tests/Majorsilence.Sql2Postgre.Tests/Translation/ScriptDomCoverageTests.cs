using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Tests.Translation;

/// <summary>
/// The guard against syntax the converter reads past without a word (WP 4.21): for every ScriptDom
/// node type the source under <c>src/</c> names, each public property must be read somewhere (its
/// name used as <c>.Name</c>, <c>Name:</c> or <c>Name =</c>) or be on the reviewed list
/// <c>ScriptDomUnreadProperties.txt</c> with the reason it is safe. The check goes by name, so a
/// property that shares its name with one the source reads elsewhere is not caught.
/// </summary>
public class ScriptDomCoverageTests
{
    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static string Root => Path.GetFullPath(Path.Combine(Here(), "..", "..", ".."));

    [Test]
    public void EveryPropertyIsReadOrReviewed()
    {
        var reviewed = File.ReadAllLines(Path.Combine(Here(), "ScriptDomUnreadProperties.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2)[0])
            .ToHashSet();
        var unread = Unread();
        Assert.Multiple(() =>
        {
            Assert.That(unread.Except(reviewed).Order(), Is.Empty,
                "ScriptDom properties the converter never reads: read each one (translate or report what it changes), or add it to ScriptDomUnreadProperties.txt with the reason it is safe.");
            Assert.That(reviewed.Except(unread).Order(), Is.Empty,
                "Listed in ScriptDomUnreadProperties.txt but now read, or no longer on a node type the source names: remove the line.");
        });
    }

    /// <summary><c>DeclaringType.Property</c> for each property of a named node type that the source never uses by name.</summary>
    private static HashSet<string> Unread()
    {
        string src = Path.Combine(Root, "src");
        string text = string.Concat(Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Select(File.ReadAllText));
        var words = Regex.Matches(text, @"\b[A-Z][A-Za-z0-9]+\b").Select(m => m.Value).ToHashSet();
        var used = Regex.Matches(text, @"(?:\.|\{\s*|,\s*)([A-Z][A-Za-z0-9]+)\b(?=\s*[:=.)\]?,;\s!])").Select(m => m.Groups[1].Value).ToHashSet();
        var fragment = typeof(TSqlFragment);
        var common = fragment.GetProperties().Select(p => p.Name).ToHashSet();
        return fragment.Assembly.GetTypes()
            .Where(t => fragment.IsAssignableFrom(t) && words.Contains(t.Name))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => !common.Contains(p.Name) && !used.Contains(p.Name))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToHashSet();
    }
}
