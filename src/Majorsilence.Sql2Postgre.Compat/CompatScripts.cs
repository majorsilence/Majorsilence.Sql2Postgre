using System.Reflection;

namespace Majorsilence.Sql2Postgre.Compat;

/// <summary>
/// The mssql_compat helpers: plain SQL and PL/pgSQL functions for T-SQL built-ins PostgreSQL has
/// no inline equivalent for (DATEDIFF's boundary counting, CHARINDEX with a start, ISNUMERIC, …).
/// Each is created by the generated script itself, in the schema the options name; no extension.
/// </summary>
public static class CompatScripts
{
    private const string Prefix = "Majorsilence.Sql2Postgre.Compat.Sql.";

    private static readonly Dictionary<string, string> Scripts = Load();

    /// <summary>Every helper, by name (<c>datediff</c>, <c>try_cast_integer</c>, …).</summary>
    public static IReadOnlyCollection<string> Names => Scripts.Keys;

    /// <summary>The CREATE OR REPLACE FUNCTION statement for a helper, in the given (already quoted) schema.</summary>
    public static string Create(string name, string quotedSchema) =>
        Scripts.TryGetValue(name, out string? sql)
            ? sql.Replace("{schema}", quotedSchema, StringComparison.Ordinal)
            : throw new ArgumentException($"There is no mssql_compat helper '{name}'.", nameof(name));

    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(CompatScripts).Assembly;
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            string name = resource[Prefix.Length..^".sql".Length];
            scripts[name] = reader.ReadToEnd().Replace("\r\n", "\n");
        }
        return scripts;
    }
}
