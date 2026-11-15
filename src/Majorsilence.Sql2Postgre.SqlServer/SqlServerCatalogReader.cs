using Majorsilence.Sql2Postgre.Model;
using Microsoft.Data.SqlClient;

namespace Majorsilence.Sql2Postgre.SqlServer;

/// <summary>
/// Reads a SQL Server database's schema and code from its catalog views (see
/// <see cref="CatalogQueries"/>) into a <see cref="SourceDatabase"/>. Read-only: it issues the
/// documented <c>SELECT</c>s and nothing else.
/// </summary>
public static class SqlServerCatalogReader
{
    public static async Task<SourceReadResult> ReadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        return await ReadAsync(conn, cancellationToken);
    }

    public static async Task<SourceReadResult> ReadAsync(SqlConnection conn, CancellationToken ct = default)
    {
        var diagnostics = new List<Diagnostic>();

        var (dbName, collation, compat) = (await QueryAsync(conn, CatalogQueries.Database,
            r => (r.GetString(0), r.GetString(1), (int)r.GetByte(2)), ct)).Single();
        var schemas = await QueryAsync(conn, CatalogQueries.Schemas, r => r.GetString(0), ct);

        var columns = (await QueryAsync(conn, CatalogQueries.Columns, r => (Id: r.GetInt32(0), Column: ReadColumn(r)), ct))
            .ToLookup(x => x.Id, x => x.Column);
        var indexColumns = (await QueryAsync(conn, CatalogQueries.IndexColumns, r => new
        {
            ObjectId = r.GetInt32(0), IndexId = r.GetInt32(1), Name = r.GetString(2), KeyOrdinal = (int)r.GetByte(3),
            Descending = r.GetBoolean(4), Included = r.GetBoolean(5), ColumnId = r.GetInt32(6),
        }, ct)).ToLookup(x => (x.ObjectId, x.IndexId));

        IReadOnlyList<IndexColumn> KeysOf(int objectId, int indexId) => indexColumns[(objectId, indexId)]
            .Where(c => c.KeyOrdinal > 0).OrderBy(c => c.KeyOrdinal).Select(c => new IndexColumn(c.Name, c.Descending)).ToList();

        var keyConstraints = (await QueryAsync(conn, CatalogQueries.KeyConstraints, r => new
        {
            ObjectId = r.GetInt32(0), Name = r.IsDBNull(1) ? null : r.GetString(1), Type = r.GetString(2).Trim(), IndexType = (int)r.GetByte(3), IndexId = r.GetInt32(4),
        }, ct)).ToLookup(x => x.ObjectId);

        var indexes = (await QueryAsync(conn, CatalogQueries.Indexes, r => new
        {
            ObjectId = r.GetInt32(0), IndexId = r.GetInt32(1), Name = r.GetString(2), Type = (int)r.GetByte(3),
            IsUnique = r.GetBoolean(4), Filter = r.IsDBNull(5) ? null : r.GetString(5),
        }, ct)).ToLookup(x => x.ObjectId);

        var fullText = (await QueryAsync(conn, CatalogQueries.FullTextIndexes, r => (Id: r.GetInt32(0), Column: r.GetString(1)), ct))
            .ToLookup(x => x.Id, x => x.Column);

        var fkColumns = (await QueryAsync(conn, CatalogQueries.ForeignKeyColumns,
            r => (FkId: r.GetInt32(0), Column: r.GetString(1), RefColumn: r.GetString(2)), ct)).ToLookup(x => x.FkId);
        var foreignKeys = (await QueryAsync(conn, CatalogQueries.ForeignKeys, r =>
        {
            int fkId = r.GetInt32(0);
            return (ObjectId: r.GetInt32(1), Fk: new ForeignKey(r.IsDBNull(2) ? null : r.GetString(2),
                fkColumns[fkId].Select(c => c.Column).ToList(), new ObjectName(r.GetString(3), r.GetString(4)),
                fkColumns[fkId].Select(c => c.RefColumn).ToList(),
                Action(r.GetByte(5)), Action(r.GetByte(6)), r.GetBoolean(7), r.GetBoolean(8)));
        }, ct)).ToLookup(x => x.ObjectId, x => x.Fk);

        var checks = (await QueryAsync(conn, CatalogQueries.CheckConstraints, r => (Id: r.GetInt32(0),
            Check: new CheckConstraint(r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4))), ct))
            .ToLookup(x => x.Id, x => x.Check);

        var tables = await QueryAsync(conn, CatalogQueries.Tables, r =>
        {
            int id = r.GetInt32(0);
            var name = new ObjectName(r.GetString(1), r.GetString(2));
            long? rows = r.IsDBNull(3) ? null : r.GetInt64(3);

            PrimaryKey? pk = null;
            var uniques = new List<UniqueConstraint>();
            foreach (var kc in keyConstraints[id])
            {
                if (kc.Type == "PK")
                    pk = new PrimaryKey(kc.Name, KeysOf(id, kc.IndexId), kc.IndexType == 1);
                else
                    uniques.Add(new UniqueConstraint(kc.Name, KeysOf(id, kc.IndexId)));
            }

            var tableIndexes = indexes[id].Select(i => new TableIndex(i.Name,
                IsColumnstore(i.Type)
                    ? indexColumns[(id, i.IndexId)].OrderBy(c => c.ColumnId).Select(c => new IndexColumn(c.Name)).ToList()
                    : KeysOf(id, i.IndexId),
                indexColumns[(id, i.IndexId)].Where(c => c.Included && !IsColumnstore(i.Type)).OrderBy(c => c.ColumnId).Select(c => c.Name).ToList(),
                i.IsUnique, i.Type is 1 or 5, i.Filter, IndexTypeName(i.Type))).ToList();
            if (fullText[id].Any())
                tableIndexes.Add(new TableIndex($"fulltext_{name.Name}", fullText[id].Select(c => new IndexColumn(c)).ToList(),
                    [], false, false, null, "fulltext"));

            return new Table(name, columns[id].OrderBy(c => c.Ordinal).ToList(), pk, uniques,
                foreignKeys[id].ToList(), checks[id].ToList(),
                tableIndexes.OrderBy(i => i.Name, StringComparer.Ordinal).ToList(), rows);
        }, ct);

        var sequences = await QueryAsync(conn, CatalogQueries.Sequences, r => new Sequence(
            new ObjectName(r.GetString(0), r.GetString(1)),
            new SqlServerType(r.GetString(2), Precision: r.GetString(2) is "decimal" or "numeric" ? r.GetByte(3) : null,
                Scale: r.GetString(2) is "decimal" or "numeric" ? r.GetByte(4) : null),
            r.GetInt64(5), r.GetInt64(6), NullableInt64(r, 7), NullableInt64(r, 8), r.GetBoolean(9), NullableInt64(r, 10)), ct);

        var userTypes = await QueryAsync(conn, CatalogQueries.UserTypes, r =>
        {
            bool isTable = r.GetBoolean(8);
            var baseType = TypeOf(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5), null, r.IsDBNull(6) ? null : r.GetString(6));
            var tableColumns = isTable && !r.IsDBNull(9) ? columns[r.GetInt32(9)].OrderBy(c => c.Ordinal).ToList() : [];
            return new UserType(new ObjectName(r.GetString(0), r.GetString(1)), baseType, r.GetBoolean(7), isTable, tableColumns);
        }, ct);

        var modules = await QueryAsync(conn, CatalogQueries.Modules, r =>
        {
            var name = new ObjectName(r.GetString(1), r.GetString(2));
            string type = r.GetString(3).Trim();
            string? definition = r.IsDBNull(4) ? null : r.GetString(4);
            ObjectName? parent = r.IsDBNull(5) ? null : new ObjectName(r.GetString(5), r.GetString(6));
            var kind = ModuleKind(type);
            if (type is "FS" or "FT" or "PC" or "TA" or "AF")
                diagnostics.Add(new Diagnostic(DiagnosticCodes.ClrObject, Severity.Error, name,
                    $"{name} is a CLR {kind.ToString().ToLowerInvariant()} (.NET code, not T-SQL); it must be rewritten by hand."));
            else if (definition is null)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.EncryptedModule, Severity.Error, name,
                    $"{name} was created WITH ENCRYPTION, so SQL Server does not reveal its text; supply the original script."));
            return new Module(name, kind, definition ?? "", parent, r.GetInt32(7) == 1);
        }, ct);

        var dependencies = await QueryAsync(conn, CatalogQueries.Dependencies, r => new ObjectDependency(
            new ObjectName(r.GetString(0), r.GetString(1)), new ObjectName(r.GetString(2), r.GetString(3))), ct);

        var db = new SourceDatabase(dbName, collation, compat, schemas, tables, sequences, userTypes, modules, dependencies);
        return new SourceReadResult(db, diagnostics);
    }

    private static Column ReadColumn(SqlDataReader r)
    {
        var type = TypeOf(r.GetString(3), r.GetInt16(5), r.GetByte(6), r.GetByte(7),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(8) ? null : r.GetString(8));
        IdentitySpec? identity = r.IsDBNull(12) ? null : new IdentitySpec(r.GetInt64(12), r.GetInt64(13));
        ComputedSpec? computed = r.IsDBNull(16) ? null : new ComputedSpec(r.GetString(16), r.GetBoolean(17));
        return new Column(r.GetString(2), r.GetInt32(1), type, r.GetBoolean(9), identity,
            r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15),
            computed, r.GetBoolean(10), r.GetBoolean(11));
    }

    /// <summary>
    /// The model's type from the catalog's numbers. <c>max_length</c> is in bytes (−1 for
    /// <c>max</c>), so Unicode types halve it; precision and scale mean something only for some
    /// types, and are dropped for the rest so two equal types compare equal.
    /// </summary>
    internal static SqlServerType TypeOf(string name, short maxLength, byte precision, byte scale, string? userType, string? collation)
    {
        int? length = name switch
        {
            "char" or "varchar" or "binary" or "varbinary" => maxLength,
            "nchar" or "nvarchar" => maxLength == -1 ? -1 : maxLength / 2,
            _ => null,
        };
        int? prec = name is "decimal" or "numeric" or "float" or "real" ? precision : null;
        int? sc = name is "decimal" or "numeric" or "datetime2" or "time" or "datetimeoffset" ? scale : null;
        return new SqlServerType(name, length, prec, sc, userType, collation);
    }

    private static ReferentialAction Action(byte code) => code switch
    {
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };

    private static bool IsColumnstore(int type) => type is 5 or 6;

    private static string IndexTypeName(int type) => type switch
    {
        3 => "xml",
        4 => "spatial",
        5 or 6 => "columnstore",
        7 => "hash",
        _ => "rowstore",
    };

    private static ObjectKind ModuleKind(string type) => type switch
    {
        "V" => ObjectKind.View,
        "P" or "PC" => ObjectKind.Procedure,
        "TR" or "TA" => ObjectKind.Trigger,
        _ => ObjectKind.Function,
    };

    private static long? NullableInt64(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static async Task<List<T>> QueryAsync<T>(SqlConnection conn, string sql, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
            list.Add(map(reader));
        return list;
    }
}
