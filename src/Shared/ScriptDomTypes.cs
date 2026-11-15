using System.Globalization;
using Majorsilence.Sql2Postgre.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Shared;

/// <summary>
/// Reads a ScriptDom data type into the model's <see cref="SqlServerType"/>, filling in what SQL
/// Server fills in when a length, precision or scale is left out. Compiled into both the reader
/// (column types in scripts) and the translator (CAST and CONVERT targets, variables), so the
/// two never disagree; a linked source file because neither project may reference the other.
/// </summary>
internal static class ScriptDomTypes
{
    /// <summary>
    /// A type as the catalog reports it. A user-named type becomes <c>alias</c> until the whole
    /// script is read (the alias may be defined in a later file); the built-in names that parse as
    /// user types — <c>sysname</c>, <c>hierarchyid</c>, <c>geography</c>, <c>geometry</c> — are
    /// system types and resolve at once.
    /// </summary>
    public static SqlServerType TypeOf(DataTypeReference type)
    {
        switch (type)
        {
            case XmlDataTypeReference:
                return new SqlServerType("xml");
            case SqlDataTypeReference sql:
                return SqlType(sql);
            case UserDataTypeReference user:
                string baseName = user.Name.BaseIdentifier.Value.ToLowerInvariant();
                if (user.Name.SchemaIdentifier is null && baseName is "sysname" or "hierarchyid" or "geography" or "geometry")
                    return new SqlServerType(baseName);
                var n = new ObjectName(user.Name.SchemaIdentifier?.Value ?? "dbo", user.Name.BaseIdentifier.Value);
                return new SqlServerType("alias", UserTypeName: $"{n.Schema}.{n.Name}");
            default:
                return new SqlServerType(type.Name.BaseIdentifier.Value.ToLowerInvariant());
        }
    }

    private static SqlServerType SqlType(SqlDataTypeReference sql)
    {
        var p = sql.Parameters;
        int? First(int whenMissing) => p.Count == 0 ? whenMissing : p[0] is MaxLiteral ? -1 : int.Parse(p[0].Value, CultureInfo.InvariantCulture);
        int? Second(int whenMissing) => p.Count < 2 ? whenMissing : int.Parse(p[1].Value, CultureInfo.InvariantCulture);
        return sql.SqlDataTypeOption switch
        {
            SqlDataTypeOption.Char => new SqlServerType("char", First(1)),
            SqlDataTypeOption.VarChar => new SqlServerType("varchar", First(1)),
            SqlDataTypeOption.NChar => new SqlServerType("nchar", First(1)),
            SqlDataTypeOption.NVarChar => new SqlServerType("nvarchar", First(1)),
            SqlDataTypeOption.Binary => new SqlServerType("binary", First(1)),
            SqlDataTypeOption.VarBinary => new SqlServerType("varbinary", First(1)),
            SqlDataTypeOption.Decimal => new SqlServerType("decimal", Precision: First(18), Scale: Second(0)),
            SqlDataTypeOption.Numeric => new SqlServerType("numeric", Precision: First(18), Scale: Second(0)),
            SqlDataTypeOption.Float => First(53) <= 24 ? new SqlServerType("real", Precision: 24) : new SqlServerType("float", Precision: 53),
            SqlDataTypeOption.Real => new SqlServerType("real", Precision: 24),
            SqlDataTypeOption.DateTime2 => new SqlServerType("datetime2", Scale: First(7)),
            SqlDataTypeOption.Time => new SqlServerType("time", Scale: First(7)),
            SqlDataTypeOption.DateTimeOffset => new SqlServerType("datetimeoffset", Scale: First(7)),
            SqlDataTypeOption.Rowversion or SqlDataTypeOption.Timestamp => new SqlServerType("timestamp"),
            SqlDataTypeOption.Sql_Variant => new SqlServerType("sql_variant"),
            _ => new SqlServerType(sql.SqlDataTypeOption.ToString().ToLowerInvariant()),
        };
    }

}
