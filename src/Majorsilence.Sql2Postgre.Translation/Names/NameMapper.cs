using System.IO.Hashing;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Translation.Names;

/// <summary>A mapped PostgreSQL schema-qualified name, both parts unquoted.</summary>
public sealed record PgName(string Schema, string Name)
{
    /// <summary>Quoted where needed and qualified: <c>sales."order"</c>.</summary>
    public string Qualified => $"{NameMapper.Quote(Schema)}.{NameMapper.Quote(Name)}";

    public override string ToString() => Qualified;
}

/// <summary>
/// Turns SQL Server names into PostgreSQL identifiers and remembers every answer, so an object is
/// called the same thing wherever it is referenced. Source names are matched case-insensitively,
/// as SQL Server's usual collations match them; the first spelling seen decides the output under
/// <see cref="NameStyle.Preserve"/>.
/// </summary>
/// <remarks>
/// PostgreSQL namespaces, each checked for collisions separately: schemas; relations per schema
/// (tables, views, sequences, indexes — including those behind primary keys and unique
/// constraints — and types, since every table also has a row type of its own name); routines per
/// schema; columns per table; check and foreign-key constraints per table; triggers per table.
/// </remarks>
public sealed partial class NameMapper(ConversionOptions options)
{
    /// <summary>PostgreSQL's identifier limit (NAMEDATALEN − 1), in bytes.</summary>
    public const int MaxIdentifierBytes = 63;

    private const int TruncatedPrefixBytes = 54;

    private readonly Dictionary<string, Namespace> _namespaces = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public string Schema(string sourceSchema)
    {
        if (options.SchemaMap.TryGetValue(sourceSchema, out string? mapped))
            return mapped;
        return Map("schemas", sourceSchema, sourceSchema, null);
    }

    /// <summary>A table, view or sequence (one namespace in SQL Server too).</summary>
    public PgName Relation(ObjectName source)
    {
        string schema = Schema(source.Schema);
        return new PgName(schema, Map($"relations:{schema}", $"object:{source.Name}", source.Name, source));
    }

    /// <summary>An alias or table type (its own namespace in SQL Server, the relations' in PostgreSQL).</summary>
    public PgName Type(ObjectName source)
    {
        string schema = Schema(source.Schema);
        return new PgName(schema, Map($"relations:{schema}", $"type:{source.Name}", source.Name, source));
    }

    /// <summary>An index, or the index behind a primary key or unique constraint (same namespace as tables).</summary>
    public string Index(ObjectName table, string sourceName) =>
        Map($"relations:{Schema(table.Schema)}", $"index:{table.Name}.{sourceName}", sourceName, table);

    public PgName Routine(ObjectName source)
    {
        string schema = Schema(source.Schema);
        return new PgName(schema, Map($"routines:{schema}", source.Name, source.Name, source));
    }

    public string Column(ObjectName table, string column) =>
        Map($"columns:{table.Schema}.{table.Name}".ToLowerInvariant(), column, column, table);

    /// <summary>A check or foreign-key constraint (unique per table in PostgreSQL).</summary>
    public string Constraint(ObjectName table, string sourceName) =>
        Map($"constraints:{table.Schema}.{table.Name}".ToLowerInvariant(), sourceName, sourceName, table);

    public string Trigger(ObjectName table, string sourceName) =>
        Map($"triggers:{table.Schema}.{table.Name}".ToLowerInvariant(), sourceName, sourceName, table);

    /// <summary>
    /// A name with no object of its own to keep unique — a table alias, a select-list alias, a
    /// column of a derived table or CTE — styled and kept within 63 bytes, but not remembered.
    /// </summary>
    public string Alias(string source)
    {
        string styled = ApplyStyle(source, options.NameStyle);
        return Truncate(styled.Length == 0 ? "_" : styled, out _);
    }

    /// <summary>Double-quotes an identifier unless PostgreSQL would read it unquoted as the same name.</summary>
    public static string Quote(string identifier) =>
        IsPlainIdentifier(identifier) ? identifier : $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>Lower-case letters, digits, <c>_</c> and <c>$</c>, not starting with a digit or <c>$</c>, and not a keyword.</summary>
    public static bool IsPlainIdentifier(string identifier) =>
        PlainIdentifier().IsMatch(identifier) && !Keywords.Contains(identifier);

    /// <summary>The style transformation alone, without truncation, collisions or memory.</summary>
    public static string ApplyStyle(string name, NameStyle style) => style switch
    {
        NameStyle.Lower => name.ToLowerInvariant(),
        NameStyle.SnakeCase => SnakeCase(name),
        _ => name,
    };

    /// <param name="space">The PostgreSQL namespace the result must be unique in.</param>
    /// <param name="key">What identifies the source object within that namespace (case-insensitive).</param>
    /// <param name="source">The source name to map.</param>
    private string Map(string space, string key, string source, ObjectName? owner)
    {
        if (!_namespaces.TryGetValue(space, out var ns))
            _namespaces[space] = ns = new Namespace();
        if (ns.BySource.TryGetValue(key, out string? known))
            return known;

        string styled = ApplyStyle(source, options.NameStyle);
        if (styled.Length == 0)
            styled = "_";
        string candidate = Truncate(styled, out bool truncated);
        if (truncated)
            _diagnostics.Add(new Diagnostic(DiagnosticCodes.IdentifierTruncated, Severity.Warning, owner,
                $"'{source}' is longer than PostgreSQL's {MaxIdentifierBytes}-byte limit; it is named '{candidate}'."));

        if (ns.Taken.Contains(candidate))
        {
            string first = candidate;
            for (int n = 2; ns.Taken.Contains(candidate); n++)
                candidate = WithSuffix(first, $"_{n}");
            _diagnostics.Add(new Diagnostic(DiagnosticCodes.IdentifierCollision, Severity.Warning, owner,
                $"'{source}' maps to '{first}', which another name already has; it is named '{candidate}'."));
        }

        ns.Taken.Add(candidate);
        ns.BySource[key] = candidate;
        return candidate;
    }

    /// <summary>Over 63 bytes: the first 54 bytes, <c>_</c>, and 8 hex digits of a hash of the whole name.</summary>
    private static string Truncate(string name, out bool truncated)
    {
        truncated = Encoding.UTF8.GetByteCount(name) > MaxIdentifierBytes;
        if (!truncated)
            return name;
        uint hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(name));
        return $"{PrefixWithinBytes(name, TruncatedPrefixBytes)}_{hash:x8}";
    }

    private static string WithSuffix(string name, string suffix)
    {
        int room = MaxIdentifierBytes - Encoding.UTF8.GetByteCount(suffix);
        return PrefixWithinBytes(name, room) + suffix;
    }

    /// <summary>The longest prefix whose UTF-8 form fits in <paramref name="maxBytes"/>, never splitting a character.</summary>
    private static string PrefixWithinBytes(string name, int maxBytes)
    {
        int bytes = 0, i = 0;
        while (i < name.Length)
        {
            int width = char.IsSurrogatePair(name, i) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(name.AsSpan(i, width));
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            i += width;
        }
        return name[..i];
    }

    /// <summary>
    /// <c>OrderID</c> → <c>order_id</c>, <c>HTTPServer</c> → <c>http_server</c>,
    /// <c>Address2Line</c> → <c>address2_line</c>, <c>Order Details</c> → <c>order_details</c>.
    /// </summary>
    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0 && sb[^1] != '_')
                    sb.Append('_');
                continue;
            }
            if (char.IsUpper(c) && sb.Length > 0 && sb[^1] != '_')
            {
                char prev = name[i - 1];
                bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                    sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim('_');
    }

    private static readonly HashSet<string> Keywords = LoadKeywords();

    /// <summary>PostgreSQL's keywords that are not unreserved (embedded <c>postgres-keywords.txt</c>).</summary>
    public static IReadOnlySet<string> NonUnreservedKeywords => Keywords;

    private static HashSet<string> LoadKeywords()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("postgres-keywords.txt")
            ?? throw new InvalidOperationException("postgres-keywords.txt is not embedded.");
        using var reader = new StreamReader(stream);
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
                words.Add(line.Split(' ')[0]);
        }
        return words;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex PlainIdentifier();

    private sealed class Namespace
    {
        public Dictionary<string, string> BySource { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Taken { get; } = new(StringComparer.Ordinal);
    }
}
