using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// What the full-text index and the queries over it share: the text search configuration a SQL
/// Server language becomes, and the <c>to_tsvector</c> expression of each column. The index has one
/// such expression per column and a query matches against the same expressions, so PostgreSQL uses
/// the index (it matches index expressions by their form).
/// </summary>
public static class FullTextSearch
{
    /// <summary>
    /// PostgreSQL's built-in text search configurations for the SQL Server languages that have one.
    /// The neutral language is <c>simple</c> (no stemming, no stop words), as is any language left
    /// out here; <c>mssql_compat.fulltext_config</c> has the same table for a language chosen at run time.
    /// </summary>
    private static readonly Dictionary<int, string> Configs = new()
    {
        [0] = "simple",
        [1025] = "arabic",
        [1027] = "catalan",
        [1030] = "danish",
        [1031] = "german",
        [1032] = "greek",
        [1033] = "english",
        [1036] = "french",
        [1040] = "italian",
        [1043] = "dutch",
        [1044] = "norwegian",
        [1046] = "portuguese",
        [1048] = "romanian",
        [1049] = "russian",
        [1053] = "swedish",
        [1055] = "turkish",
        [1057] = "indonesian",
        [1063] = "lithuanian",
        [1081] = "hindi",
        [1097] = "tamil",
        [2057] = "english",
        [2070] = "portuguese",
        [2074] = "serbian",
        [3082] = "spanish",
        [3098] = "serbian",
    };

    /// <summary>Every mapped LCID with its configuration, for the test that compares this table with the helper's.</summary>
    public static IReadOnlyDictionary<int, string> Mapped => Configs;

    /// <summary>The configuration for a language (null: the server default, English), or null when none fits.</summary>
    public static string? Config(int? lcid) => Configs.GetValueOrDefault(lcid ?? FullTextLanguages.English);

    /// <summary>The configuration for a column of a full-text index; <c>simple</c> for a language with none (S2P2021).</summary>
    public static string ColumnConfig(FullTextColumn column) => Config(column.Language) ?? "simple";

    /// <summary>
    /// Whether a column's values can be indexed as text: the string types and xml. Documents in
    /// varbinary or image columns need SQL Server's filters for their file type, which PostgreSQL
    /// does not have.
    /// </summary>
    public static bool IsText(SqlServerType type) =>
        type.Name is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" or "sysname";

    /// <summary>
    /// <c>to_tsvector('config', column)</c>; an xml column as its text, whose tags (attributes
    /// included) the parser skips, so only element content is indexed.
    /// </summary>
    public static PgExpr Vector(string config, PgExpr column, SqlServerType type) =>
        new PgFunctionCall("to_tsvector", [PgLiteral.String(config), type.Name == "xml" ? new PgCast(column, "text") : column]);
}
