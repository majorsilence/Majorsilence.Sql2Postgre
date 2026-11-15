using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <summary>One object read back from a script file (see <see cref="ScriptPlanner.ReadScripts"/>).</summary>
public sealed record ScriptUnit(string File, ObjectKind Kind, string TargetName, ObjectStatus Status, string Sql);

/// <summary>
/// Orders converted objects for applying, writes them as one script per phase plus a JSON report,
/// and reads such scripts back. Within a phase an object comes after the objects it depends on
/// (ties in target-name order); a cycle is emitted in name order with <c>S2P0202</c>.
/// </summary>
public static partial class ScriptPlanner
{
    public const string ReportFile = "report.json";

    public static string FileName(Phase phase) => $"{(int)phase + 1:00}-{phase switch
    {
        Phase.Schemas => "schemas",
        Phase.Types => "types",
        Phase.Sequences => "sequences",
        Phase.Tables => "tables",
        Phase.Compat => "compat",
        Phase.Code => "code",
        Phase.Indexes => "indexes",
        Phase.Constraints => "constraints",
        Phase.ForeignKeys => "foreign-keys",
        Phase.Triggers => "triggers",
        _ => phase.ToString().ToLowerInvariant(),
    }}.sql";

    public static (IReadOnlyList<ConvertedObject> Objects, IReadOnlyList<Diagnostic> Diagnostics) Order(IEnumerable<ConvertedObject> objects)
    {
        var ordered = new List<ConvertedObject>();
        var diagnostics = new List<Diagnostic>();
        foreach (var phase in objects.GroupBy(o => o.Phase).OrderBy(g => g.Key))
            ordered.AddRange(TopologicalOrder(phase.ToList(), diagnostics));
        return (ordered, diagnostics);
    }

    /// <summary>Kahn's algorithm over the dependencies inside one phase, always taking the smallest ready name.</summary>
    private static IEnumerable<ConvertedObject> TopologicalOrder(List<ConvertedObject> phase, List<Diagnostic> diagnostics)
    {
        var bySource = phase.GroupBy(o => Key(o.Source)).ToDictionary(g => g.Key, g => g.ToList());
        var pending = phase.ToDictionary(o => o, o => o.DependsOn
            .Where(d => bySource.ContainsKey(Key(d)))
            .SelectMany(d => bySource[Key(d)])
            .Where(d => !ReferenceEquals(d, o))
            .ToHashSet(ReferenceEqualityComparer.Instance));
        var comparer = Comparer<ConvertedObject>.Create((a, b) =>
        {
            int c = string.CompareOrdinal(a.TargetName, b.TargetName);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });
        var result = new List<ConvertedObject>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => p.Value.Count == 0).Select(p => p.Key).Order(comparer).FirstOrDefault();
            if (ready is null)
            {
                var cycle = pending.Keys.Order(comparer).ToList();
                diagnostics.Add(new Diagnostic(DiagnosticCodes.DependencyCycle, Severity.Warning, cycle[0].Source,
                    $"These objects depend on each other in a cycle and are emitted in name order: {string.Join(", ", cycle.Select(o => o.TargetName))}."));
                result.AddRange(cycle);
                break;
            }
            result.Add(ready);
            pending.Remove(ready);
            foreach (var deps in pending.Values)
                deps.Remove(ready);
        }
        return result;
    }

    private static string Key(ObjectName name) => $"{name.Schema}.{name.Name}".ToLowerInvariant();

    /// <summary>The marker line that starts each object in a script.</summary>
    public static string Marker(ConvertedObject o) => $"-- sql2pg:object {o.Kind} {o.TargetName} status={o.Status}";

    /// <summary>
    /// Writes <c>01-schemas.sql</c> … <c>10-triggers.sql</c> (every phase, even when empty, so the
    /// file set is always the same) and <c>report.json</c>. Skipped objects appear as their marker
    /// and a note only. Code runs with <c>check_function_bodies</c> off so PL/pgSQL bodies can be
    /// created in any order.
    /// </summary>
    public static void WriteFiles(ConversionResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var phase in Enum.GetValues<Phase>())
            File.WriteAllText(Path.Combine(directory, FileName(phase)), PhaseScript(result, phase), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, ReportFile), Report(result), new UTF8Encoding(false));
    }

    public static string PhaseScript(ConversionResult result, Phase phase)
    {
        var sb = new StringBuilder($"-- sql2pg: phase {phase}\n");
        if (phase == Phase.Code)
            sb.Append("SET check_function_bodies = off;\n");
        foreach (var o in result.Objects.Where(o => o.Phase == phase))
        {
            sb.Append('\n').Append(Marker(o)).Append('\n');
            if (o.Status == ObjectStatus.Skipped)
                sb.Append(string.Concat(o.Diagnostics.Select(d => $"-- {d.Code}: {d.Message}\n")));
            sb.Append(o.Sql);
        }
        return sb.ToString();
    }

    public static string Report(ConversionResult result) => JsonSerializer.Serialize(result, ConversionOptions.Json);

    /// <summary>Splits phase scripts back into objects on their marker lines, in file and then text order.</summary>
    public static IReadOnlyList<ScriptUnit> ReadScripts(string directory)
    {
        var units = new List<ScriptUnit>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.sql").Order(StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file).Replace("\r\n", "\n");
            var markers = MarkerLine().Matches(text);
            string preamble = markers.Count > 0 ? text[..markers[0].Index] : text;
            string setup = string.Concat(preamble.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).Select(l => l + "\n"));
            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                int start = m.Index + m.Length;
                int end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
                string body = text[start..end].Trim('\n');
                units.Add(new ScriptUnit(Path.GetFileName(file), Enum.Parse<ObjectKind>(m.Groups[1].Value), m.Groups[2].Value,
                    Enum.Parse<ObjectStatus>(m.Groups[3].Value), setup + (body.Length > 0 ? body + "\n" : "")));
            }
        }
        return units;
    }

    [GeneratedRegex(@"^-- sql2pg:object (\w+) (.+) status=(\w+)$", RegexOptions.Multiline)]
    private static partial Regex MarkerLine();
}
