using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Majorsilence.Sql2Postgre.CodeConversion;

/// <summary>A source file to convert: its full path, and its path relative to the input root (the output mirrors it).</summary>
public sealed record CodeFile(string FullPath, string RelativePath);

/// <summary>
/// Finds the C# and VB files to convert from a path: a file, a folder (recursively, without
/// <c>bin</c> and <c>obj</c>), a project (<c>.csproj</c>/<c>.vbproj</c>: the files under its
/// folder, as SDK-style projects compile them by default), or a solution (<c>.sln</c>/<c>.slnx</c>:
/// its projects). The root is the folder the relative paths start from.
/// </summary>
public static partial class CodeInput
{
    public static (string Root, IReadOnlyList<CodeFile> Files) Discover(string path)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
            return (full, Under(full, full));
        if (!File.Exists(full))
            throw new FileNotFoundException($"'{path}' is not a file or folder.", full);
        string root = Path.GetDirectoryName(full)!;
        return Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".cs" or ".vb" => (root, [new CodeFile(full, Path.GetFileName(full))]),
            ".csproj" or ".vbproj" => (root, Project(full, root)),
            ".sln" => (root, Projects(SlnProjects(full), root)),
            ".slnx" => (root, Projects(SlnxProjects(full), root)),
            var other => throw new ArgumentException($"'{path}' is a {other} file; give a .cs/.vb file, a folder, a project or a solution."),
        };
    }

    private static IReadOnlyList<CodeFile> Projects(IEnumerable<string> projects, string root) =>
        projects.Where(File.Exists).SelectMany(p => Project(p, root)).DistinctBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyList<CodeFile> Project(string project, string root)
    {
        string extension = Path.GetExtension(project).Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ? ".vb" : ".cs";
        var files = Under(Path.GetDirectoryName(project)!, root)
            .Where(f => f.FullPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || f.FullPath.Equals(project, StringComparison.OrdinalIgnoreCase)).ToList();
        // Central package versions apply to the project too.
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(project)!); dir is not null; dir = dir.Parent)
        {
            string props = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(props))
            {
                if (Path.GetRelativePath(root, props) is var rel && !rel.StartsWith("..", StringComparison.Ordinal))
                    files.Add(new CodeFile(props, rel));
                break;
            }
        }
        return files;
    }

    private static List<CodeFile> Under(string folder, string root) =>
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => (CodeLanguages.For(f) is not null || ProjectFiles.Is(f)) && !InBuildOutput(Path.GetRelativePath(folder, f)))
            .Order(StringComparer.Ordinal)
            .Select(f => new CodeFile(f, Path.GetRelativePath(root, f)))
            .ToList();

    private static bool InBuildOutput(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SlnProjects(string sln)
    {
        string folder = Path.GetDirectoryName(sln)!;
        foreach (Match m in SlnProject().Matches(File.ReadAllText(sln)))
            yield return Path.GetFullPath(Path.Combine(folder, m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)));
    }

    private static IEnumerable<string> SlnxProjects(string slnx)
    {
        string folder = Path.GetDirectoryName(slnx)!;
        return XDocument.Load(slnx).Descendants("Project").Select(p => (string?)p.Attribute("Path")).OfType<string>()
            .Select(p => Path.GetFullPath(Path.Combine(folder, p.Replace('\\', Path.DirectorySeparatorChar))));
    }

    [GeneratedRegex(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+\.(cs|vb)proj)""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SlnProject();
}
