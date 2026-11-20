using System.Globalization;
using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <summary>
/// The rows of <c>mssql_compat.object_names</c> (the <c>catalog</c> helper): every schema, table,
/// view, routine, sequence and column of the source with its SQL Server and PostgreSQL names, and
/// a column's SQL Server type as sys.columns and INFORMATION_SCHEMA.COLUMNS report it.
/// </summary>
public static class CatalogNames
{
    public static string Statement(SemanticModel model, NameMapper names, string quotedSchema)
    {
        var db = model.Source;
        var rows = new List<string>();
        void Row(string kind, ObjectName source, string? sourceColumn, PgName target, string? targetColumn,
            int? columnId = null, SqlServerType? type = null, bool? nullable = null, bool? identity = null)
        {
            string Text(string? v) => v is null ? "NULL" : "'" + v.Replace("'", "''") + "'";
            string Int(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
            string Bool(bool? v) => v is null ? "NULL" : v.Value ? "true" : "false";
            rows.Add($"({Text(kind)}, {Text(source.Schema)}, {Text(source.Name)}, {Text(sourceColumn)}, {Text(target.Schema)}, {Text(target.Name)}, {Text(targetColumn)}, "
                     + $"{Int(columnId)}, {Text(type?.Name)}, {Int(type is null ? null : MaxLength(type))}, {Int(type is null ? null : CharacterLength(type))}, "
                     + $"{Int(type is null ? null : Precision(type))}, {Int(type is null ? null : Scale(type))}, {Bool(nullable)}, {Bool(identity)})");
        }

        foreach (string schema in db.Schemas.Append(SemanticModel.DefaultSchema).Distinct(StringComparer.OrdinalIgnoreCase))
            Row("SCHEMA", new ObjectName(schema, schema), null, new PgName(names.Schema(schema), names.Schema(schema)), null);
        foreach (var t in db.Tables)
        {
            Row("U", t.Name, null, names.Relation(t.Name), null);
            foreach (var c in t.Columns)
                Row("COLUMN", t.Name, c.Name, names.Relation(t.Name), names.Column(t.Name, c.Name), c.Ordinal, c.Type, c.IsNullable, c.Identity is not null);
        }
        foreach (var m in db.Modules)
        {
            switch (m.Kind)
            {
                case ObjectKind.View:
                    Row("V", m.Name, null, names.Relation(m.Name), null);
                    var columns = model.Relation(m.Name)?.Columns ?? [];
                    for (int i = 0; i < columns.Count; i++)
                        Row("COLUMN", m.Name, columns[i].Name, names.Relation(m.Name), names.Column(m.Name, columns[i].Name), i + 1, columns[i].Type, true, false);
                    break;
                case ObjectKind.Procedure:
                    Row("P", m.Name, null, names.Routine(m.Name), null);
                    break;
                case ObjectKind.Function:
                    string kind = model.ModuleTree(m.Name) is FunctionStatementBody { ReturnType: var r }
                        ? r switch { SelectFunctionReturnType => "IF", TableValuedFunctionReturnType => "TF", _ => "FN" }
                        : "FN";
                    Row(kind, m.Name, null, names.Routine(m.Name), null);
                    break;
            }
        }
        foreach (var s in db.Sequences)
            Row("SO", s.Name, null, names.Relation(s.Name), null);

        var sb = new StringBuilder($"DELETE FROM {quotedSchema}.object_names;\n");
        if (rows.Count > 0)
            sb.Append($"INSERT INTO {quotedSchema}.object_names VALUES\n    {string.Join(",\n    ", rows)};\n");
        var indexes = IndexRows(db, names);
        sb.Append($"DELETE FROM {quotedSchema}.index_names;\n");
        if (indexes.Count > 0)
            sb.Append($"INSERT INTO {quotedSchema}.index_names VALUES\n    {string.Join(",\n    ", indexes)};\n");
        return sb.ToString();
    }

    /// <summary>
    /// The sys.indexes rows of each table: its primary key, unique constraints and indexes (not
    /// full-text ones, which sys.indexes does not list), with the PostgreSQL index each became, and a
    /// heap row (index_id 0) for a table without a clustered index. The clustered index is 1, the
    /// others are numbered from 2 in the order the source lists them (SQL Server's ids differ).
    /// </summary>
    private static List<string> IndexRows(SourceDatabase db, NameMapper names)
    {
        var rows = new List<string>();
        static string Text(string? v) => v is null ? "NULL" : "'" + v.Replace("'", "''") + "'";
        static string Bool(bool v) => v ? "true" : "false";
        foreach (var t in db.Tables)
        {
            string schema = names.Relation(t.Name).Schema;
            var generated = new Dictionary<string, int>(StringComparer.Ordinal);
            // As the schema translator names constraints SQL Server named itself.
            string Generated(string baseName)
            {
                int n = generated.GetValueOrDefault(baseName);
                generated[baseName] = n + 1;
                return n == 0 ? baseName : $"{baseName}{n}";
            }
            var entries = new List<(string? Name, string Target, bool Clustered, int Type, bool Unique, bool Primary, bool Constraint, bool Filter)>();
            if (t.PrimaryKey is { } pk)
            {
                string target = names.Index(t.Name, pk.Name ?? Generated($"{t.Name.Name}_pkey"));
                entries.Add((pk.Name ?? target, target, pk.IsClustered, pk.IsClustered ? 1 : 2, true, true, false, false));
            }
            foreach (var u in t.Uniques)
            {
                string target = names.Index(t.Name, u.Name ?? Generated($"{t.Name.Name}_{string.Join("_", u.Columns.Select(c => c.Name))}_key"));
                entries.Add((u.Name ?? target, target, false, 2, true, false, true, false));
            }
            foreach (var ix in t.Indexes.Where(ix => ix.Type is "rowstore" or "spatial"))
                entries.Add((ix.Name, names.Index(t.Name, ix.Name), ix.IsClustered, ix.Type == "spatial" ? 4 : ix.IsClustered ? 1 : 2,
                    ix.IsUnique, false, false, ix.FilterExpression is not null));

            int next = 2;
            if (!entries.Any(e => e.Clustered))
                rows.Add($"({Text(t.Name.Schema)}, {Text(t.Name.Name)}, NULL, {Text(schema)}, NULL, 0, 0, false, false, false, false)");
            foreach (var e in entries)
                rows.Add($"({Text(t.Name.Schema)}, {Text(t.Name.Name)}, {Text(e.Name)}, {Text(schema)}, {Text(e.Target)}, {(e.Clustered ? 1 : next++)}, "
                         + $"{e.Type}, {Bool(e.Unique)}, {Bool(e.Primary)}, {Bool(e.Constraint)}, {Bool(e.Filter)})");
        }
        return rows;
    }

    /// <summary>sys.columns.max_length: bytes; 2 per character for the Unicode types; -1 for max.</summary>
    public static int? MaxLength(SqlServerType t) => t.Name switch
    {
        _ when t.IsMax => -1,
        "nchar" or "nvarchar" => (t.Length ?? 1) * 2,
        "char" or "varchar" or "binary" or "varbinary" => t.Length ?? 1,
        "sysname" => 256,
        "bit" or "tinyint" => 1,
        "smallint" => 2,
        "int" or "real" or "smalldatetime" or "smallmoney" => 4,
        "bigint" or "float" or "money" or "datetime" or "timestamp" or "rowversion" => 8,
        "date" => 3,
        "time" => (t.Scale ?? 7) <= 2 ? 3 : (t.Scale ?? 7) <= 4 ? 4 : 5,
        "datetime2" => (t.Scale ?? 7) <= 2 ? 6 : (t.Scale ?? 7) <= 4 ? 7 : 8,
        "datetimeoffset" => (t.Scale ?? 7) <= 2 ? 8 : (t.Scale ?? 7) <= 4 ? 9 : 10,
        "decimal" or "numeric" => (t.Precision ?? 18) <= 9 ? 5 : (t.Precision ?? 18) <= 19 ? 9 : (t.Precision ?? 18) <= 28 ? 13 : 17,
        "uniqueidentifier" => 16,
        "text" or "ntext" or "image" => 16,
        "xml" or "geography" or "geometry" or "hierarchyid" => -1,
        "sql_variant" => 8016,
        _ => null,
    };

    /// <summary>INFORMATION_SCHEMA.COLUMNS.CHARACTER_MAXIMUM_LENGTH: characters for the string types, -1 for max.</summary>
    public static int? CharacterLength(SqlServerType t) => t.Name switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" => t.IsMax ? -1 : t.Length ?? 1,
        "sysname" => 128,
        _ => null,
    };

    private static int? Precision(SqlServerType t) => t.Name switch
    {
        "decimal" or "numeric" => t.Precision ?? 18,
        "tinyint" => 3, "smallint" => 5, "int" => 10, "bigint" => 19,
        "money" => 19, "smallmoney" => 10, "float" => 53, "real" => 24, "bit" => 1,
        _ => null,
    };

    private static int? Scale(SqlServerType t) => t.Name switch
    {
        "decimal" or "numeric" => t.Scale ?? 0,
        "tinyint" or "smallint" or "int" or "bigint" or "bit" => 0,
        "money" or "smallmoney" => 4,
        _ => null,
    };
}
