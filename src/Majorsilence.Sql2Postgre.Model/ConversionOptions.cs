using System.Text.Json;
using System.Text.Json.Serialization;

namespace Majorsilence.Sql2Postgre.Model;

public enum NameStyle { Lower, SnakeCase, Preserve }

public enum CaseInsensitiveStrategy { Report, IcuCollation }

public enum ResultSetStyle { Function, Refcursor }

/// <summary>The choices a user makes once per migration. Read from <c>sql2pg.json</c> by the CLI.</summary>
public sealed record ConversionOptions
{
    public const int MinTargetVersion = 15;
    public const int MaxTargetVersion = 19;

    /// <summary>The PostgreSQL major version the output is written for (15–19; 18 by default).</summary>
    public int TargetVersion { get; init; } = 18;

    public NameStyle NameStyle { get; init; } = NameStyle.Lower;

    public CaseInsensitiveStrategy CaseInsensitive { get; init; } = CaseInsensitiveStrategy.Report;

    public ResultSetStyle ResultSetStyle { get; init; } = ResultSetStyle.Function;

    public bool UseCompatSchema { get; init; } = true;

    public string CompatSchemaName { get; init; } = "mssql_compat";

    /// <summary>SQL Server schema → PostgreSQL schema. Schemas not listed keep their (mapped) name.</summary>
    public IReadOnlyDictionary<string, string> SchemaMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["dbo"] = "public" };

    /// <summary><c>schema.table.column</c> → the PostgreSQL type to use for it, verbatim.</summary>
    public IReadOnlyDictionary<string, string> TypeOverrides { get; init; } = new Dictionary<string, string>();

    /// <summary><c>schema.name</c> patterns of objects to leave out; <c>*</c> matches any run of characters.</summary>
    public IReadOnlyList<string> ExcludeObjects { get; init; } = [];

    /// <summary>
    /// Columns (<c>schema.table.column</c>) to give a pg_trgm index, besides those the converted
    /// views and routines search with a leading-wildcard LIKE. Needs the <c>pg_trgm</c> extension.
    /// </summary>
    public IReadOnlyList<string> TrigramIndexes { get; init; } = [];

    /// <summary>The permitted extensions the output may use (a subset of <see cref="PermittedExtensions.All"/>; empty = core only).</summary>
    [JsonConverter(typeof(SortedStringSetConverter))]
    public IReadOnlySet<string> Extensions { get; init; } = PermittedExtensions.All;

    public bool AllowsExtension(string name) => Extensions.Contains(name);

    /// <summary>What is wrong with these options; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (TargetVersion is < MinTargetVersion or > MaxTargetVersion)
            errors.Add($"targetVersion must be between {MinTargetVersion} and {MaxTargetVersion}; got {TargetVersion}.");
        foreach (string ext in Extensions.Where(e => !PermittedExtensions.All.Contains(e)).Order(StringComparer.Ordinal))
            errors.Add($"Extension '{ext}' is not permitted. Permitted: {string.Join(", ", PermittedExtensions.All)}.");
        if (string.IsNullOrWhiteSpace(CompatSchemaName))
            errors.Add("compatSchemaName must not be empty.");
        return errors;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ConversionOptions FromJson(string json) =>
        JsonSerializer.Deserialize<ConversionOptions>(json, Json) ?? new ConversionOptions();
}

/// <summary>
/// A string set as a JSON array, sorted ordinally on write so the file is deterministic.
/// System.Text.Json cannot create an <see cref="IReadOnlySet{T}"/> on its own.
/// </summary>
public sealed class SortedStringSetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new SortedSet<string>(JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [], StringComparer.Ordinal);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Order(StringComparer.Ordinal).ToArray(), options);
}

/// <summary>
/// The extensions the maintainer has permitted the output to use. Any other needs the
/// maintainer's approval, case by case, before it is added here.
/// </summary>
public static class PermittedExtensions
{
    public const string Hstore = "hstore";
    public const string PgCron = "pg_cron";
    public const string PgStatStatements = "pg_stat_statements";
    public const string PgTrgm = "pg_trgm";
    public const string PgCrypto = "pgcrypto";
    public const string PostGis = "postgis";
    public const string UuidOssp = "uuid-ossp";
    public const string Vector = "vector";

    public static readonly IReadOnlySet<string> All = new SortedSet<string>(StringComparer.Ordinal)
        { Hstore, PgCron, PgStatStatements, PgTrgm, PgCrypto, PostGis, UuidOssp, Vector };
}
