-- The SQL Server catalog seen through the converted database. The converter fills object_names
-- with every schema, table, view, routine, sequence and column it converted: its SQL Server and
-- PostgreSQL names and, for a column, its SQL Server type. The views (sys.*, INFORMATION_SCHEMA.*)
-- and functions (OBJECT_ID, SCHEMA_NAME, COL_LENGTH, …) answer with SQL Server's names. Ids are
-- PostgreSQL oids, so the views and functions agree with each other, not with SQL Server's ids.
-- Objects created after the conversion are not listed.
CREATE TABLE IF NOT EXISTS {schema}.object_names (
    kind text NOT NULL,
    source_schema text NOT NULL,
    source_name text NOT NULL,
    source_column text,
    target_schema text NOT NULL,
    target_name text NOT NULL,
    target_column text,
    column_id integer,
    type_name text,
    max_length smallint,
    char_length integer,
    numeric_precision smallint,
    numeric_scale smallint,
    is_nullable boolean,
    is_identity boolean
);

-- Each object (kind U, V, P, FN, IF, TF, SO) with its PostgreSQL oid and schema oid.
CREATE OR REPLACE VIEW {schema}.catalog_objects AS
SELECT o.kind, o.source_schema, o.source_name, o.target_schema, o.target_name,
       CAST(CASE WHEN o.kind IN ('U', 'V', 'SO') THEN CAST(to_regclass(format('%I.%I', o.target_schema, o.target_name)) AS oid)
            ELSE (SELECT min(p.oid) FROM pg_proc AS p JOIN pg_namespace AS n ON n.oid = p.pronamespace
                  WHERE n.nspname = o.target_schema AND p.proname = o.target_name) END AS integer) AS object_id,
       CAST((SELECT n.oid FROM pg_namespace AS n WHERE n.nspname = o.target_schema) AS integer) AS schema_id
FROM {schema}.object_names AS o
WHERE o.kind NOT IN ('SCHEMA', 'COLUMN');

CREATE OR REPLACE VIEW {schema}.sys_objects AS
SELECT c.source_name AS name, c.object_id, CAST(NULL AS integer) AS principal_id, c.schema_id, 0 AS parent_object_id,
       CAST(rpad(c.kind, 2) AS char(2)) AS type,
       CASE c.kind WHEN 'U' THEN 'USER_TABLE' WHEN 'V' THEN 'VIEW' WHEN 'P' THEN 'SQL_STORED_PROCEDURE'
           WHEN 'FN' THEN 'SQL_SCALAR_FUNCTION' WHEN 'IF' THEN 'SQL_INLINE_TABLE_VALUED_FUNCTION'
           WHEN 'TF' THEN 'SQL_TABLE_VALUED_FUNCTION' WHEN 'SO' THEN 'SEQUENCE_OBJECT' END AS type_desc,
       false AS is_ms_shipped
FROM {schema}.catalog_objects AS c
WHERE c.object_id IS NOT NULL;

CREATE OR REPLACE VIEW {schema}.sys_tables AS SELECT * FROM {schema}.sys_objects WHERE type = 'U';
CREATE OR REPLACE VIEW {schema}.sys_views AS SELECT * FROM {schema}.sys_objects WHERE type = 'V';
CREATE OR REPLACE VIEW {schema}.sys_procedures AS SELECT * FROM {schema}.sys_objects WHERE type = 'P';

-- current_value: the last value handed out, or the start value before the first.
CREATE OR REPLACE VIEW {schema}.sys_sequences AS
SELECT o.*, s.start_value, s.increment_by AS increment, COALESCE(s.last_value, s.start_value) AS current_value
FROM {schema}.sys_objects AS o
JOIN {schema}.catalog_objects AS c ON c.object_id = o.object_id
JOIN pg_sequences AS s ON s.schemaname = c.target_schema AND s.sequencename = c.target_name
WHERE o.type = 'SO';

CREATE OR REPLACE VIEW {schema}.sys_schemas AS
SELECT o.source_schema AS name, CAST(n.oid AS integer) AS schema_id, 1 AS principal_id
FROM {schema}.object_names AS o
JOIN pg_namespace AS n ON n.nspname = o.target_schema
WHERE o.kind = 'SCHEMA';

CREATE OR REPLACE VIEW {schema}.sys_columns AS
SELECT t.object_id, c.source_column AS name, c.column_id, c.max_length, CAST(COALESCE(c.numeric_precision, 0) AS smallint) AS "precision",
       CAST(COALESCE(c.numeric_scale, 0) AS smallint) AS scale, c.is_nullable, c.is_identity
FROM {schema}.object_names AS c
JOIN {schema}.catalog_objects AS t ON t.kind IN ('U', 'V') AND t.source_schema = c.source_schema AND t.source_name = c.source_name
WHERE c.kind = 'COLUMN' AND t.object_id IS NOT NULL;

CREATE OR REPLACE VIEW {schema}.information_schema_tables AS
SELECT CAST(current_database() AS text) AS table_catalog, c.source_schema AS table_schema, c.source_name AS table_name,
       CASE c.kind WHEN 'U' THEN 'BASE TABLE' ELSE 'VIEW' END AS table_type
FROM {schema}.catalog_objects AS c
WHERE c.kind IN ('U', 'V') AND c.object_id IS NOT NULL;

CREATE OR REPLACE VIEW {schema}.information_schema_views AS
SELECT table_catalog, table_schema, table_name FROM {schema}.information_schema_tables WHERE table_type = 'VIEW';

CREATE OR REPLACE VIEW {schema}.information_schema_columns AS
SELECT CAST(current_database() AS text) AS table_catalog, c.source_schema AS table_schema, c.source_name AS table_name,
       c.source_column AS column_name, c.column_id AS ordinal_position, CAST(NULL AS text) AS column_default,
       CASE WHEN c.is_nullable THEN 'YES' ELSE 'NO' END AS is_nullable, c.type_name AS data_type,
       c.char_length AS character_maximum_length, c.numeric_precision, CAST(c.numeric_scale AS integer) AS numeric_scale
FROM {schema}.object_names AS c
JOIN {schema}.catalog_objects AS t ON t.kind IN ('U', 'V') AND t.source_schema = c.source_schema AND t.source_name = c.source_name
WHERE c.kind = 'COLUMN' AND t.object_id IS NOT NULL;

CREATE OR REPLACE VIEW {schema}.information_schema_routines AS
SELECT CAST(current_database() AS text) AS specific_catalog, c.source_schema AS specific_schema, c.source_name AS specific_name,
       CAST(current_database() AS text) AS routine_catalog, c.source_schema AS routine_schema, c.source_name AS routine_name,
       CASE c.kind WHEN 'P' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS routine_type
FROM {schema}.catalog_objects AS c
WHERE c.kind IN ('P', 'FN', 'IF', 'TF') AND c.object_id IS NOT NULL;

CREATE OR REPLACE VIEW {schema}.information_schema_schemata AS
SELECT CAST(current_database() AS text) AS catalog_name, name AS schema_name FROM {schema}.sys_schemas;

-- SCHEMA_NAME(id): the SQL Server name of a schema (its PostgreSQL name when not converted).
CREATE OR REPLACE FUNCTION {schema}.schema_name(id integer) RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE
RETURN (SELECT COALESCE((SELECT o.source_schema FROM {schema}.object_names AS o WHERE o.kind = 'SCHEMA' AND o.target_schema = n.nspname LIMIT 1), n.nspname)
        FROM pg_namespace AS n WHERE n.oid = CAST(id AS oid));

-- SCHEMA_ID(name), by the SQL Server name (case-insensitive, as SQL Server's default collation compares).
CREATE OR REPLACE FUNCTION {schema}.schema_id(name text) RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE
RETURN (SELECT CAST(n.oid AS integer) FROM pg_namespace AS n
        WHERE n.nspname = COALESCE((SELECT o.target_schema FROM {schema}.object_names AS o
                                    WHERE o.kind = 'SCHEMA' AND lower(o.source_schema) = lower(name) LIMIT 1), name));

-- OBJECT_ID('[db.]schema.name' [, type]): the oid of the converted object, NULL when there is none.
-- An unqualified name is looked up in dbo, the usual default schema.
CREATE OR REPLACE FUNCTION {schema}.object_id(name text, object_type text DEFAULT NULL) RETURNS integer
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
DECLARE
    parts text[];
    n integer;
BEGIN
    IF name IS NULL THEN
        RETURN NULL;
    END IF;
    parts := ARRAY(SELECT CASE WHEN m[1] LIKE '[%' THEN substr(m[1], 2, length(m[1]) - 2)
                               WHEN m[1] LIKE '"%' THEN substr(m[1], 2, length(m[1]) - 2) ELSE m[1] END
                   FROM regexp_matches(replace(name, '..', '.dbo.'), '(\[[^\]]*\]|"[^"]*"|[^.]+)', 'g') AS m);
    n := cardinality(parts);
    RETURN (SELECT c.object_id FROM {schema}.catalog_objects AS c
            WHERE lower(c.source_name) = lower(parts[n]) AND lower(c.source_schema) = lower(COALESCE(parts[n - 1], 'dbo'))
              AND (object_type IS NULL OR c.kind = upper(rtrim(object_type)))
            LIMIT 1);
END $$;

-- OBJECT_NAME(id): the SQL Server name of a converted object.
CREATE OR REPLACE FUNCTION {schema}.object_name(id integer) RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE
RETURN (SELECT c.source_name FROM {schema}.catalog_objects AS c WHERE c.object_id = id LIMIT 1);

-- COL_LENGTH('table', 'column'): the column's length in bytes, as SQL Server stores it (-1 for max).
CREATE OR REPLACE FUNCTION {schema}.col_length(table_name text, column_name text) RETURNS smallint
LANGUAGE sql STABLE PARALLEL SAFE
RETURN (SELECT c.max_length FROM {schema}.sys_columns AS c
        WHERE c.object_id = {schema}.object_id(table_name) AND lower(c.name) = lower(column_name));

-- COLUMNPROPERTY(id, 'column', property): IsIdentity, AllowsNull, ColumnId, Precision, Scale.
CREATE OR REPLACE FUNCTION {schema}.columnproperty(id integer, column_name text, property text) RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE
RETURN (SELECT CASE lower(property)
                   WHEN 'isidentity' THEN CAST(c.is_identity AS integer)
                   WHEN 'allowsnull' THEN CAST(c.is_nullable AS integer)
                   WHEN 'columnid' THEN c.column_id
                   WHEN 'precision' THEN c."precision"
                   WHEN 'scale' THEN c.scale
               END
        FROM {schema}.sys_columns AS c WHERE c.object_id = id AND lower(c.name) = lower(column_name));

-- OBJECTPROPERTY(id, property): the Is… properties of an object's kind; NULL for an unknown object or property.
CREATE OR REPLACE FUNCTION {schema}.objectproperty(id integer, property text) RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE
RETURN (SELECT CASE lower(property)
                   WHEN 'isusertable' THEN CAST(c.kind = 'U' AS integer)
                   WHEN 'istable' THEN CAST(c.kind = 'U' AS integer)
                   WHEN 'isview' THEN CAST(c.kind = 'V' AS integer)
                   WHEN 'isprocedure' THEN CAST(c.kind = 'P' AS integer)
                   WHEN 'isscalarfunction' THEN CAST(c.kind = 'FN' AS integer)
                   WHEN 'isinlinefunction' THEN CAST(c.kind = 'IF' AS integer)
                   WHEN 'istablefunction' THEN CAST(c.kind IN ('IF', 'TF') AS integer)
                   WHEN 'ismsshipped' THEN 0
                   WHEN 'schemaid' THEN c.schema_id
               END
        FROM {schema}.catalog_objects AS c WHERE c.object_id = id);
