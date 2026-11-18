using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Schema;
using Microsoft.Data.SqlClient;
using NpgsqlTypes;

namespace Majorsilence.Sql2Postgre.Data;

/// <summary>How one column is read from SQL Server and written to PostgreSQL.</summary>
/// <param name="SourceSelect">The column's expression in the source SELECT (more than one for spatial: value and SRID).</param>
/// <param name="Read">Reads the value at the reader's ordinal and returns what to write (null for NULL), counting changes in <paramref name="Read"/>'s state.</param>
public sealed record ColumnPlan(Column Source, string TargetName, string PgType, NpgsqlDbType DbType,
    IReadOnlyList<string> SourceSelect, Func<SqlDataReader, int, ColumnState, object?> Read);

/// <summary>Per-column counts of changes made while copying, reported as diagnostics afterwards.</summary>
public sealed class ColumnState
{
    public long NulsRemoved;
    public long ZmDropped;
}

/// <summary>What to copy for one table: the source columns, the target table and how each value crosses over.</summary>
public sealed record TablePlan(Table Source, PgName Target, IReadOnlyList<ColumnPlan> Columns)
{
    public string SourceName => $"[{Source.Name.Schema.Replace("]", "]]")}].[{Source.Name.Name.Replace("]", "]]")}]";

    /// <summary>The integer primary key column a big table can be split on, if it has one.</summary>
    public Column? SplitColumn => Source.PrimaryKey is { Columns.Count: 1 } pk
        && Source.Columns.FirstOrDefault(c => c.Name == pk.Columns[0].Name) is { Type.Name: "tinyint" or "smallint" or "int" or "bigint" } c
            ? c : null;

    public string SelectSql(string? where = null) =>
        $"SELECT {string.Join(", ", Columns.SelectMany(c => c.SourceSelect))} FROM {SourceName}{(where is null ? "" : " WHERE " + where)}";

    public string CopySql =>
        $"COPY {Target.Qualified} ({string.Join(", ", Columns.Select(c => NameMapper.Quote(c.TargetName)))}) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// Builds a plan per table from the same translation the schema came from, so target names
    /// and types are exactly the ones created. Computed columns are skipped (PostgreSQL computes its
    /// generated columns), as are columns the schema translation left out.
    /// </summary>
    public static IReadOnlyList<TablePlan> Build(SourceDatabase db, IReadOnlyList<TranslatedObject> translated, NameMapper names)
    {
        var domains = translated.SelectMany(o => o.Statements).OfType<PgCreateDomain>()
            .ToDictionary(d => d.Name.Qualified, d => d.BaseType, StringComparer.Ordinal);
        var createdTables = translated.Where(o => o.Kind == ObjectKind.Table && !o.Skipped)
            .SelectMany(o => o.Statements.OfType<PgCreateTable>().Select(t => (o.Source, t)))
            .ToDictionary(x => x.Source, x => x.t);

        var plans = new List<TablePlan>();
        foreach (var table in db.Tables)
        {
            if (!createdTables.TryGetValue(table.Name, out var created))
                continue;
            var columns = new List<ColumnPlan>();
            foreach (var column in table.Columns.Where(c => c.Computed is null))
            {
                string target = names.Column(table.Name, column.Name);
                var def = created.Columns.FirstOrDefault(c => c.Name == target && c.Generated is null);
                if (def is null)
                    continue;
                string pgType = domains.TryGetValue(def.Type, out var baseType) ? baseType : def.Type;
                columns.Add(PlanColumn(column, target, pgType));
            }
            plans.Add(new TablePlan(table, created.Name, columns));
        }
        return plans;
    }

    private static string Col(Column c) => $"[{c.Name.Replace("]", "]]")}]";

    private static ColumnPlan PlanColumn(Column c, string target, string pgType)
    {
        string baseType = pgType.Split('(')[0].Trim();
        string src = Col(c);
        switch (c.Type.Name)
        {
            case "bit":
                return new(c, target, pgType, NpgsqlDbType.Boolean, [src], (r, i, _) => r.IsDBNull(i) ? null : r.GetBoolean(i));
            case "tinyint":
                return new(c, target, pgType, NpgsqlDbType.Smallint, [src], (r, i, _) => r.IsDBNull(i) ? null : (short)r.GetByte(i));
            case "smallint":
                return new(c, target, pgType, NpgsqlDbType.Smallint, [src], (r, i, _) => r.IsDBNull(i) ? null : r.GetInt16(i));
            case "int":
                return new(c, target, pgType, NpgsqlDbType.Integer, [src], (r, i, _) => r.IsDBNull(i) ? null : r.GetInt32(i));
            case "bigint":
                return new(c, target, pgType, baseType == "integer" ? NpgsqlDbType.Integer : NpgsqlDbType.Bigint, [src],
                    (r, i, _) => r.IsDBNull(i) ? null : baseType == "integer" ? checked((int)r.GetInt64(i)) : r.GetInt64(i));
            case "decimal" or "numeric" when baseType is "integer" or "bigint":
                // An identity on decimal(p,0) became an integer column.
                return new(c, target, pgType, baseType == "integer" ? NpgsqlDbType.Integer : NpgsqlDbType.Bigint, [src],
                    (r, i, _) => r.IsDBNull(i) ? null : baseType == "integer" ? (object)decimal.ToInt32(r.GetDecimal(i)) : decimal.ToInt64(r.GetDecimal(i)));
            case "decimal" or "numeric" or "money" or "smallmoney":
                return new(c, target, pgType, NpgsqlDbType.Numeric, [src], (r, i, _) => r.IsDBNull(i) ? null : r.GetDecimal(i));
            case "float":
                return new(c, target, pgType, baseType == "real" ? NpgsqlDbType.Real : NpgsqlDbType.Double, [src],
                    (r, i, _) => r.IsDBNull(i) ? null : baseType == "real" ? (float)r.GetDouble(i) : r.GetDouble(i));
            case "real":
                return new(c, target, pgType, NpgsqlDbType.Real, [src], (r, i, _) => r.IsDBNull(i) ? null : r.GetFloat(i));
            case "date":
                return new(c, target, pgType, NpgsqlDbType.Date, [src], (r, i, _) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i)));
            case "time":
                return new(c, target, pgType, NpgsqlDbType.Time, [src],
                    (r, i, _) => r.IsDBNull(i) ? null : ValueTransforms.RoundToMicroseconds(r.GetTimeSpan(i)));
            case "datetime" or "smalldatetime" or "datetime2":
                return new(c, target, pgType, NpgsqlDbType.Timestamp, [src],
                    (r, i, _) => r.IsDBNull(i) ? null : ValueTransforms.RoundToMicroseconds(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Unspecified)));
            case "datetimeoffset":
                return new(c, target, pgType, NpgsqlDbType.TimestampTz, [src],
                    (r, i, _) => r.IsDBNull(i) ? null : ValueTransforms.ToUtc(r.GetDateTimeOffset(i)));
            case "uniqueidentifier":
                return new(c, target, pgType, NpgsqlDbType.Uuid, [src], (r, i, _) => r.IsDBNull(i) ? null : r.GetGuid(i));
            case "binary" or "varbinary" or "image" or "timestamp" or "rowversion":
                return new(c, target, pgType, NpgsqlDbType.Bytea, [src], (r, i, _) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i));
            case "xml":
                return new(c, target, pgType, NpgsqlDbType.Xml, [$"CAST({src} AS nvarchar(max))"], StringReader);
            case "sql_variant" or "hierarchyid":
                return new(c, target, pgType, NpgsqlDbType.Text, [$"CAST({src} AS nvarchar(4000))"], StringReader);
            case "geography" or "geometry" when baseType is "geography" or "geometry":
                bool geography = baseType == "geography";
                return new(c, target, pgType, NpgsqlDbType.Bytea,
                    [$"{src}.STAsBinary()", $"{src}.STSrid", $"CAST({src}.HasZ | {src}.HasM AS bit)"],
                    (r, i, s) =>
                    {
                        if (r.IsDBNull(i))
                            return null;
                        // The reader is sequential: the value, its SRID and its Z/M flag in column order.
                        var wkb = (byte[])r.GetValue(i);
                        int srid = r.GetInt32(i + 1);
                        if (!r.IsDBNull(i + 2) && r.GetBoolean(i + 2))
                            Interlocked.Increment(ref s.ZmDropped);
                        var ewkb = ValueTransforms.ToEwkb(wkb, srid);
                        return geography ? ValueTransforms.NudgeGeodetic(ewkb) : ewkb;
                    });
            case "geography" or "geometry":
                return new(c, target, pgType, NpgsqlDbType.Bytea, [$"{src}.STAsBinary()"], (r, i, _) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i));
            case "vector" when baseType == "vector":
                return new(c, target, pgType, NpgsqlDbType.Bytea, [$"CAST({src} AS nvarchar(max))"],
                    (r, i, _) => r.IsDBNull(i) ? null : ValueTransforms.ToPgvectorBinary(ValueTransforms.ParseVector(r.GetString(i))));
            case "vector":
                return new(c, target, pgType, NpgsqlDbType.Array | NpgsqlDbType.Real, [$"CAST({src} AS nvarchar(max))"],
                    (r, i, _) => r.IsDBNull(i) ? null : ValueTransforms.ParseVector(r.GetString(i)));
            default:
                // char, varchar, nchar, nvarchar, text, ntext, sysname, and anything mapped to text.
                return new(c, target, pgType, baseType switch
                {
                    "char" => NpgsqlDbType.Char,
                    "varchar" => NpgsqlDbType.Varchar,
                    _ => NpgsqlDbType.Text,
                }, [c.Type.Name is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" ? src : $"CAST({src} AS nvarchar(max))"], StringReader);
        }
    }

    private static object? StringReader(SqlDataReader r, int i, ColumnState state)
    {
        if (r.IsDBNull(i))
            return null;
        string value = ValueTransforms.RemoveNul(r.GetString(i), out int removed);
        if (removed > 0)
            Interlocked.Add(ref state.NulsRemoved, removed);
        return value;
    }
}
