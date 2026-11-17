using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Microsoft.CodeAnalysis.Text;

namespace Majorsilence.Sql2Postgre.CodeConversion;

/// <summary>
/// Project files (<c>.csproj</c>, <c>.vbproj</c>) and central package versions
/// (<c>Directory.Packages.props</c>): the SQL Server packages become their Npgsql counterparts,
/// the EF Core provider at the version that matches the EF Core major. The file's text is edited
/// in place, so everything else in it stays as it was.
/// </summary>
public static partial class ProjectFiles
{
    public static bool Is(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".vbproj"
        || Path.GetFileName(path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

    /// <summary>Npgsql's EF Core provider and Npgsql itself for each EF Core major version.</summary>
    private static readonly Dictionary<int, (string Provider, string Npgsql)> Versions = new()
    {
        [8] = ("8.0.11", "8.0.9"),
        [9] = ("9.0.4", "9.0.5"),
        [10] = ("10.0.3", "10.0.3"),
    };

    private static readonly Dictionary<string, string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.EntityFrameworkCore.SqlServer"] = "Npgsql.EntityFrameworkCore.PostgreSQL",
        ["Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite"] = "Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite",
        ["Microsoft.Data.SqlClient"] = "Npgsql",
        ["System.Data.SqlClient"] = "Npgsql",
    };

    public static CodeFileResult Convert(string relativePath, string text)
    {
        var edits = new List<CodeEdit>();
        var diagnostics = new List<Diagnostic>();
        var lines = SourceText.From(text).Lines;
        // The EF Core major the project uses decides the provider's and Npgsql's versions.
        int? efMajor = Elements().Matches(text).Select(m => (Include: Attribute(m.Value, "Include"), Version: Attribute(m.Value, "Version") ?? Attribute(m.Value, "VersionOverride")))
            .Where(p => p.Include?.Value.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) == true && p.Version is not null)
            .Select(p => int.TryParse(p.Version!.Value.Split('.')[0], out int major) ? major : (int?)null).FirstOrDefault(m => m is not null);
        bool hasEf = Elements().Matches(text).Any(m => Attribute(m.Value, "Include")?.Value.Equals("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase) == true);

        foreach (Match element in Elements().Matches(text))
        {
            if (Attribute(element.Value, "Include") is not { } include || !Packages.TryGetValue(include.Value, out var replacement))
                continue;
            edits.Add(new CodeEdit(new TextSpan(element.Index + include.Index, include.Length), replacement, $"{include.Value} → {replacement}"));
            var version = Attribute(element.Value, "Version") ?? Attribute(element.Value, "VersionOverride");
            string? newVersion = null;
            if (version is not null)
            {
                int major = int.TryParse(version.Value.Split('.')[0], out int m) ? m : 0;
                if (replacement.StartsWith("Npgsql.EntityFrameworkCore", StringComparison.Ordinal))
                    newVersion = Versions.TryGetValue(major, out var v) ? v.Provider : null;
                else
                    newVersion = Versions.TryGetValue(efMajor ?? 10, out var v) ? v.Npgsql : null;
                if (newVersion is not null)
                    edits.Add(new CodeEdit(new TextSpan(element.Index + version.Index, version.Length), newVersion, $"version {version.Value} → {newVersion}"));
            }
            var position = lines.GetLinePosition(element.Index);
            diagnostics.Add(new Diagnostic(DiagnosticCodes.PackageReplaced, newVersion is null && version is not null ? Severity.Warning : Severity.Info,
                new ObjectName("", relativePath),
                newVersion is null && version is not null
                    ? $"{include.Value} {version.Value} becomes {replacement}; set its version by hand (the matching release for this EF Core version is not known here)."
                    : $"{include.Value} becomes {replacement}{(newVersion is null ? "" : " " + newVersion)}.",
                new Model.SourceSpan(position.Line + 1, position.Character + 1, element.Length)));
            if (hasEf && include.Value.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) && version is not null)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.PackageReplaced, Severity.Info, new ObjectName("", relativePath),
                    "The EF Core provider brings Npgsql with it; this reference can go if the code uses Npgsql only through EF Core.",
                    new Model.SourceSpan(position.Line + 1, position.Character + 1, element.Length)));
        }
        string converted;
        try
        {
            converted = EditEngine.Apply(text, edits);
        }
        catch (OverlappingEditsException ex)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCodes.InternalError, Severity.Error, new ObjectName("", relativePath), ex.Message));
            return new CodeFileResult(relativePath, "MSBuild", text, [], diagnostics);
        }
        return new CodeFileResult(relativePath, "MSBuild", converted, edits, diagnostics);
    }

    /// <summary>An attribute's value, with its position relative to the element.</summary>
    private sealed record AttributeValue(string Value, int Index, int Length);

    private static AttributeValue? Attribute(string element, string name)
    {
        var m = Regex.Match(element, $@"\b{name}\s*=\s*(""(?<v>[^""]*)""|'(?<v>[^']*)')");
        return m.Success ? new AttributeValue(m.Groups["v"].Value, m.Groups["v"].Index, m.Groups["v"].Length) : null;
    }

    [GeneratedRegex(@"<(PackageReference|PackageVersion)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Elements();
}
