using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// The SQL Server catalog views the converter answers, each with the mssql_compat view that stands
/// in for it (created by the <c>catalog</c> helper). The views give SQL Server's names, read from
/// <c>mssql_compat.object_names</c>, which the converter fills with every object's SQL Server and
/// PostgreSQL names; ids are PostgreSQL oids, so they match between the views and OBJECT_ID.
/// </summary>
public static class CatalogViews
{
    /// <summary>The compat helper file that creates the table, views and functions.</summary>
    public const string Helper = "catalog";

    private static SqlServerType Name => new("nvarchar", 128);
    private static SqlServerType Int => new("int");
    private static SqlServerType Bit => new("bit");
    private static SqlServerType Text(int length) => new("nvarchar", length);

    private static readonly ColumnInfo[] ObjectColumns =
    [
        new("name", Name), new("object_id", Int), new("principal_id", Int), new("schema_id", Int), new("parent_object_id", Int),
        new("type", new SqlServerType("char", 2)), new("type_desc", Text(60)), new("is_ms_shipped", Bit),
    ];

    private static readonly Dictionary<string, (string View, ColumnInfo[] Columns)> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sys.objects"] = ("sys_objects", ObjectColumns),
        ["sys.tables"] = ("sys_tables", ObjectColumns),
        ["sys.views"] = ("sys_views", ObjectColumns),
        ["sys.procedures"] = ("sys_procedures", ObjectColumns),
        ["sys.sequences"] = ("sys_sequences", [.. ObjectColumns, new("start_value", new SqlServerType("bigint")), new("increment", new SqlServerType("bigint")),
            new("current_value", new SqlServerType("bigint"))]),
        ["sys.schemas"] = ("sys_schemas", [new("name", Name), new("schema_id", Int), new("principal_id", Int)]),
        ["sys.columns"] = ("sys_columns", [new("object_id", Int), new("name", Name), new("column_id", Int), new("max_length", new SqlServerType("smallint")),
            new("precision", new SqlServerType("tinyint")), new("scale", new SqlServerType("tinyint")), new("is_nullable", Bit), new("is_identity", Bit)]),
        ["sys.indexes"] = ("sys_indexes", [new("object_id", Int), new("name", Name), new("index_id", Int), new("type", new SqlServerType("tinyint")),
            new("type_desc", Text(60)), new("is_unique", Bit), new("is_primary_key", Bit), new("is_unique_constraint", Bit), new("is_disabled", Bit),
            new("has_filter", Bit)]),
        ["sys.database_principals"] = ("sys_database_principals", [new("name", Name), new("principal_id", Int), new("type", new SqlServerType("char", 1)),
            new("type_desc", Text(60)), new("default_schema_name", Name), new("is_fixed_role", Bit)]),
        ["sys.database_role_members"] = ("sys_database_role_members", [new("role_principal_id", Int), new("member_principal_id", Int)]),
        ["INFORMATION_SCHEMA.TABLES"] = ("information_schema_tables", [new("TABLE_CATALOG", Name), new("TABLE_SCHEMA", Name), new("TABLE_NAME", Name), new("TABLE_TYPE", new SqlServerType("varchar", 10))]),
        ["INFORMATION_SCHEMA.VIEWS"] = ("information_schema_views", [new("TABLE_CATALOG", Name), new("TABLE_SCHEMA", Name), new("TABLE_NAME", Name)]),
        ["INFORMATION_SCHEMA.COLUMNS"] = ("information_schema_columns", [new("TABLE_CATALOG", Name), new("TABLE_SCHEMA", Name), new("TABLE_NAME", Name),
            new("COLUMN_NAME", Name), new("ORDINAL_POSITION", Int), new("COLUMN_DEFAULT", Text(4000)), new("IS_NULLABLE", new SqlServerType("varchar", 3)),
            new("DATA_TYPE", Name), new("CHARACTER_MAXIMUM_LENGTH", Int), new("NUMERIC_PRECISION", new SqlServerType("tinyint")), new("NUMERIC_SCALE", Int)]),
        ["INFORMATION_SCHEMA.ROUTINES"] = ("information_schema_routines", [new("SPECIFIC_CATALOG", Name), new("SPECIFIC_SCHEMA", Name), new("SPECIFIC_NAME", Name),
            new("ROUTINE_CATALOG", Name), new("ROUTINE_SCHEMA", Name), new("ROUTINE_NAME", Name), new("ROUTINE_TYPE", Text(20))]),
        ["INFORMATION_SCHEMA.SCHEMATA"] = ("information_schema_schemata", [new("CATALOG_NAME", Name), new("SCHEMA_NAME", Name)]),
    };

    private const string Audit = "is SQL Server Audit, which core PostgreSQL does not have (the server log records statements with log_statement; pgaudit is not an extension the converter may use)";
    private const string Partitioning = "describes partition functions and schemes, which PostgreSQL does not have: a table is partitioned declaratively (PARTITION BY), "
        + "and the converter does not partition tables";
    private const string Files = "describes database files and filegroups: PostgreSQL keeps a database in its data directory (tablespaces are the nearest thing)";
    private const string FullText = "describes full-text catalogs and indexes: the converter makes full-text indexes GIN indexes (S2P3042), which no view lists";
    private const string InMemory = "describes In-Memory OLTP, which PostgreSQL does not have (SERVERPROPERTY('IsXTPSupported') is 0)";
    private const string Logins = "describes logins: PostgreSQL roles are the server's (sys.database_principals lists them)";

    /// <summary>Catalog views of SQL Server features PostgreSQL does not have, with why they are not answered.</summary>
    private static readonly Dictionary<string, string> Unavailable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sys.server_audits"] = Audit,
        ["sys.server_audit_specifications"] = Audit,
        ["sys.server_audit_specification_details"] = Audit,
        ["sys.database_audit_specifications"] = Audit,
        ["sys.database_audit_specification_details"] = Audit,
        ["sys.partition_functions"] = Partitioning,
        ["sys.partition_schemes"] = Partitioning,
        ["sys.partition_range_values"] = Partitioning,
        ["sys.partition_parameters"] = Partitioning,
        ["sys.filegroups"] = Files,
        ["sys.database_files"] = Files,
        ["sys.master_files"] = Files,
        ["sys.data_spaces"] = Files,
        ["sys.destination_data_spaces"] = Files,
        ["sys.fulltext_catalogs"] = FullText,
        ["sys.fulltext_indexes"] = FullText,
        ["sys.fulltext_index_columns"] = FullText,
        ["sys.fulltext_stoplists"] = FullText,
        ["sys.memory_optimized_tables_internal_attributes"] = InMemory,
        ["sys.hash_indexes"] = InMemory,
        ["sys.server_principals"] = Logins,
        ["sys.sql_logins"] = Logins,
        ["sys.server_role_members"] = Logins,
        ["sys.configurations"] = "is the server's configuration: PostgreSQL's settings are in pg_settings, under other names",
        ["sys.syslanguages"] = "lists the server's languages, which PostgreSQL does not have (dates and messages follow lc_time and lc_messages)",
        ["sys.languages"] = "lists the server's languages, which PostgreSQL does not have (dates and messages follow lc_time and lc_messages)",
    };

    /// <summary>
    /// Why a catalog view is not answered: the SQL Server feature it describes when PostgreSQL has
    /// none, or the list of views the converter answers.
    /// </summary>
    public static string WhyNot(string schema, string name) =>
        Unavailable.TryGetValue($"{schema}.{name}", out string? reason)
            ? $"{schema}.{name} {reason}"
            : name.StartsWith("dm_", StringComparison.OrdinalIgnoreCase)
                ? $"{schema}.{name} is a dynamic management view: PostgreSQL's statistics views (pg_stat_*) have other columns"
                : $"the catalog view {schema}.{name} is not answered (the converter answers {string.Join(", ", Names)})";

    /// <summary>Whether a schema is a SQL Server catalog schema (<c>sys</c>, <c>INFORMATION_SCHEMA</c>).</summary>
    public static bool IsCatalogSchema(string? schema) =>
        string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase) || string.Equals(schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);

    /// <summary>The stand-in view and columns for a catalog view, or null when it is not one the converter answers.</summary>
    public static (string View, IReadOnlyList<ColumnInfo> Columns)? Find(string schema, string name) =>
        Views.TryGetValue($"{schema}.{name}", out var v) ? (v.View, v.Columns) : null;

    /// <summary>The catalog views answered, for the diagnostics and docs.</summary>
    public static IEnumerable<string> Names => Views.Keys.Order(StringComparer.OrdinalIgnoreCase);
}
