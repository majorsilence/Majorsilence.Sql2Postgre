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
        ["INFORMATION_SCHEMA.TABLES"] = ("information_schema_tables", [new("TABLE_CATALOG", Name), new("TABLE_SCHEMA", Name), new("TABLE_NAME", Name), new("TABLE_TYPE", new SqlServerType("varchar", 10))]),
        ["INFORMATION_SCHEMA.VIEWS"] = ("information_schema_views", [new("TABLE_CATALOG", Name), new("TABLE_SCHEMA", Name), new("TABLE_NAME", Name)]),
        ["INFORMATION_SCHEMA.COLUMNS"] = ("information_schema_columns", [new("TABLE_CATALOG", Name), new("TABLE_SCHEMA", Name), new("TABLE_NAME", Name),
            new("COLUMN_NAME", Name), new("ORDINAL_POSITION", Int), new("COLUMN_DEFAULT", Text(4000)), new("IS_NULLABLE", new SqlServerType("varchar", 3)),
            new("DATA_TYPE", Name), new("CHARACTER_MAXIMUM_LENGTH", Int), new("NUMERIC_PRECISION", new SqlServerType("tinyint")), new("NUMERIC_SCALE", Int)]),
        ["INFORMATION_SCHEMA.ROUTINES"] = ("information_schema_routines", [new("SPECIFIC_CATALOG", Name), new("SPECIFIC_SCHEMA", Name), new("SPECIFIC_NAME", Name),
            new("ROUTINE_CATALOG", Name), new("ROUTINE_SCHEMA", Name), new("ROUTINE_NAME", Name), new("ROUTINE_TYPE", Text(20))]),
        ["INFORMATION_SCHEMA.SCHEMATA"] = ("information_schema_schemata", [new("CATALOG_NAME", Name), new("SCHEMA_NAME", Name)]),
    };

    /// <summary>Whether a schema is a SQL Server catalog schema (<c>sys</c>, <c>INFORMATION_SCHEMA</c>).</summary>
    public static bool IsCatalogSchema(string? schema) =>
        string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase) || string.Equals(schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);

    /// <summary>The stand-in view and columns for a catalog view, or null when it is not one the converter answers.</summary>
    public static (string View, IReadOnlyList<ColumnInfo> Columns)? Find(string schema, string name) =>
        Views.TryGetValue($"{schema}.{name}", out var v) ? (v.View, v.Columns) : null;

    /// <summary>The catalog views answered, for the diagnostics and docs.</summary>
    public static IEnumerable<string> Names => Views.Keys.Order(StringComparer.OrdinalIgnoreCase);
}
