namespace Majorsilence.Sql2Postgre.SqlServer;

/// <summary>
/// Every query the catalog reader runs, one per concern, all against the catalog views of the
/// current database. Objects SQL Server itself ships (<c>is_ms_shipped = 1</c>) are excluded.
/// </summary>
/// <remarks>
/// Constraint names SQL Server generated itself (<c>PK__order__…</c>, different every time the
/// script runs) are read as NULL: the translator then names the constraint the way PostgreSQL
/// would, so the output does not depend on generated names.
/// </remarks>
internal static class CatalogQueries
{
    public const string Database = """
        SELECT DB_NAME() AS name,
               CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)) AS collation_name,
               d.compatibility_level
        FROM sys.databases d WHERE d.database_id = DB_ID();
        """;

    // User schemas: below 16384 are schemas, above are the fixed database roles.
    public const string Schemas = """
        SELECT s.name FROM sys.schemas s
        WHERE s.schema_id < 16384 AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
        ORDER BY s.name;
        """;

    public const string Tables = """
        SELECT t.object_id, SCHEMA_NAME(t.schema_id) AS schema_name, t.name,
               (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0, 1)) AS row_count,
               ps.name AS period_start, pe.name AS period_end,
               OBJECT_SCHEMA_NAME(t.history_table_id) AS history_schema, OBJECT_NAME(t.history_table_id) AS history_name
        FROM sys.tables t
        LEFT JOIN sys.periods sp ON sp.object_id = t.object_id
        LEFT JOIN sys.columns ps ON ps.object_id = sp.object_id AND ps.column_id = sp.start_column_id
        LEFT JOIN sys.columns pe ON pe.object_id = sp.object_id AND pe.column_id = sp.end_column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY schema_name, t.name;
        """;

    // A column's type: a system type (including the system aliases such as sysname and the CLR
    // system types hierarchyid, geography and geometry) by its own name; a user alias type by its
    // base type, with the alias recorded separately.
    public const string Columns = """
        SELECT c.object_id, c.column_id, c.name,
               CASE WHEN ut.is_user_defined = 1 THEN TYPE_NAME(c.system_type_id) ELSE ut.name END AS type_name,
               CASE WHEN ut.is_user_defined = 1 THEN SCHEMA_NAME(ut.schema_id) + '.' + ut.name END AS user_type_name,
               c.max_length, c.precision, c.scale, c.collation_name, c.is_nullable,
               c.is_rowguidcol, c.is_sparse,
               CAST(ic.seed_value AS bigint) AS identity_seed, CAST(ic.increment_value AS bigint) AS identity_increment,
               dc.definition AS default_definition, CASE WHEN dc.is_system_named = 0 THEN dc.name END AS default_name,
               cc.definition AS computed_definition, cc.is_persisted
        FROM sys.columns c
        JOIN sys.types ut ON ut.user_type_id = c.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        JOIN sys.objects o ON o.object_id = c.object_id
        WHERE (o.type = 'U' AND o.is_ms_shipped = 0) OR o.type = 'TT'
        ORDER BY c.object_id, c.column_id;
        """;

    public const string KeyConstraints = """
        SELECT kc.parent_object_id AS object_id, CASE WHEN kc.is_system_named = 0 THEN kc.name END AS name,
               kc.type, i.type AS index_type, i.index_id
        FROM sys.key_constraints kc
        JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        JOIN sys.tables t ON t.object_id = kc.parent_object_id AND t.is_ms_shipped = 0
        ORDER BY kc.parent_object_id, kc.type, kc.name;
        """;

    // Indexes that are not a primary key or unique constraint. Heaps (type 0) are not indexes.
    public const string Indexes = """
        SELECT i.object_id, i.index_id, i.name, i.type, i.is_unique, i.filter_definition
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
        WHERE i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.is_hypothetical = 0
        ORDER BY i.object_id, i.name;
        """;

    public const string IndexColumns = """
        SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column, ic.index_column_id
        FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables t ON t.object_id = ic.object_id AND t.is_ms_shipped = 0
        ORDER BY ic.object_id, ic.index_id, ic.key_ordinal, ic.index_column_id;
        """;

    /// <summary>
    /// Full-text index columns, with each column's language and type column, the key index's
    /// column (it has exactly one) and the stoplist: id 0 is the system stoplist, NULL is OFF.
    /// </summary>
    public const string FullTextIndexes = """
        SELECT fi.object_id, c.name, fic.column_id, fic.language_id, tc.name AS type_column,
               kc.name AS key_column,
               CASE WHEN fi.stoplist_id IS NULL THEN 'OFF' WHEN fi.stoplist_id = 0 THEN 'SYSTEM' ELSE sl.name END AS stoplist
        FROM sys.fulltext_indexes fi
        JOIN sys.fulltext_index_columns fic ON fic.object_id = fi.object_id
        JOIN sys.columns c ON c.object_id = fic.object_id AND c.column_id = fic.column_id
        LEFT JOIN sys.columns tc ON tc.object_id = fic.object_id AND tc.column_id = fic.type_column_id
        LEFT JOIN sys.index_columns kic ON kic.object_id = fi.object_id AND kic.index_id = fi.unique_index_id AND kic.key_ordinal = 1
        LEFT JOIN sys.columns kc ON kc.object_id = kic.object_id AND kc.column_id = kic.column_id
        LEFT JOIN sys.fulltext_stoplists sl ON sl.stoplist_id = fi.stoplist_id
        ORDER BY fi.object_id, fic.column_id;
        """;

    public const string ForeignKeys = """
        SELECT fk.object_id AS fk_id, fk.parent_object_id AS object_id,
               CASE WHEN fk.is_system_named = 0 THEN fk.name END AS name,
               SCHEMA_NAME(rt.schema_id) AS ref_schema, rt.name AS ref_table,
               fk.delete_referential_action, fk.update_referential_action, fk.is_disabled, fk.is_not_trusted
        FROM sys.foreign_keys fk
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        WHERE fk.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name;
        """;

    public const string ForeignKeyColumns = """
        SELECT fkc.constraint_object_id AS fk_id, pc.name AS column_name, rc.name AS ref_column_name
        FROM sys.foreign_key_columns fkc
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;
        """;

    public const string CheckConstraints = """
        SELECT cc.parent_object_id AS object_id, CASE WHEN cc.is_system_named = 0 THEN cc.name END AS name,
               cc.definition, cc.is_disabled, cc.is_not_trusted
        FROM sys.check_constraints cc
        WHERE cc.is_ms_shipped = 0
        ORDER BY cc.parent_object_id, cc.name;
        """;

    // A decimal or numeric sequence can range past bigint (PostgreSQL sequences cannot); its
    // values are clamped to bigint's range so the read never overflows. current_value is the
    // last value handed out (last_used_value), NULL for a sequence never used.
    public const string Sequences = """
        SELECT SCHEMA_NAME(s.schema_id) AS schema_name, s.name, TYPE_NAME(s.system_type_id) AS type_name,
               s.precision, s.scale,
               CAST(v.start_value AS bigint) AS start_value, CAST(v.increment AS bigint) AS increment,
               CAST(v.minimum_value AS bigint) AS minimum_value, CAST(v.maximum_value AS bigint) AS maximum_value,
               s.is_cycling, CAST(v.current_value AS bigint) AS current_value
        FROM sys.sequences s
        CROSS APPLY (SELECT
            CASE WHEN x.s > 9223372036854775807 THEN 9223372036854775807 WHEN x.s < -9223372036854775808 THEN -9223372036854775808 ELSE x.s END AS start_value,
            CASE WHEN x.i > 9223372036854775807 THEN 9223372036854775807 WHEN x.i < -9223372036854775808 THEN -9223372036854775808 ELSE x.i END AS increment,
            CASE WHEN x.mn > 9223372036854775807 THEN 9223372036854775807 WHEN x.mn < -9223372036854775808 THEN -9223372036854775808 ELSE x.mn END AS minimum_value,
            CASE WHEN x.mx > 9223372036854775807 THEN 9223372036854775807 WHEN x.mx < -9223372036854775808 THEN -9223372036854775808 ELSE x.mx END AS maximum_value,
            CASE WHEN x.cv > 9223372036854775807 THEN 9223372036854775807 WHEN x.cv < -9223372036854775808 THEN -9223372036854775808 ELSE x.cv END AS current_value
            FROM (SELECT CAST(s.start_value AS decimal(38,0)) AS s, CAST(s.increment AS decimal(38,0)) AS i,
                         CAST(s.minimum_value AS decimal(38,0)) AS mn, CAST(s.maximum_value AS decimal(38,0)) AS mx,
                         CAST(s.last_used_value AS decimal(38,0)) AS cv) x) v
        WHERE s.is_ms_shipped = 0
        ORDER BY schema_name, s.name;
        """;

    public const string UserTypes = """
        SELECT SCHEMA_NAME(t.schema_id) AS schema_name, t.name, TYPE_NAME(t.system_type_id) AS base_type,
               t.max_length, t.precision, t.scale, t.collation_name, t.is_nullable, t.is_table_type,
               tt.type_table_object_id
        FROM sys.types t
        LEFT JOIN sys.table_types tt ON tt.user_type_id = t.user_type_id
        WHERE t.is_user_defined = 1 AND t.is_assembly_type = 0
        ORDER BY schema_name, t.name;
        """;

    // Views, T-SQL functions, procedures and DML triggers with their text, plus the CLR kinds
    // (FS, FT, PC, TA, AF) so they can be reported. definition is NULL for an encrypted module.
    public const string Modules = """
        SELECT o.object_id, SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.type,
               m.definition,
               SCHEMA_NAME(p.schema_id) AS parent_schema, p.name AS parent_name,
               CASE WHEN o.type = 'V' AND EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = o.object_id AND i.type = 1) THEN 1 ELSE 0 END AS is_indexed
        FROM sys.objects o
        LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
        LEFT JOIN sys.objects p ON p.object_id = o.parent_object_id AND o.type IN ('TR', 'TA')
        WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'FN', 'IF', 'TF', 'P', 'TR', 'FS', 'FT', 'PC', 'TA', 'AF')
        ORDER BY schema_name, o.name;
        """;

    // References inside this database only; cross-database and linked-server references are
    // found when the module's T-SQL is parsed.
    public const string Dependencies = """
        SELECT DISTINCT SCHEMA_NAME(src.schema_id) AS from_schema, src.name AS from_name,
               SCHEMA_NAME(dst.schema_id) AS to_schema, dst.name AS to_name
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects src ON src.object_id = d.referencing_id
        JOIN sys.objects dst ON dst.object_id = d.referenced_id
        WHERE d.referenced_database_name IS NULL AND d.referenced_server_name IS NULL
          AND src.is_ms_shipped = 0 AND dst.is_ms_shipped = 0 AND d.referencing_id <> d.referenced_id
        ORDER BY from_schema, from_name, to_schema, to_name;
        """;
}
