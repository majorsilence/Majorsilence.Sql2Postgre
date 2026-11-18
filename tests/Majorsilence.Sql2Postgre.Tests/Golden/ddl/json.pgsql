-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.isjson status=Converted
-- ISJSON(s): 1 when s is a JSON object or array, 0 otherwise, NULL for NULL.
CREATE OR REPLACE FUNCTION mssql_compat.isjson(s text) RETURNS integer
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
BEGIN
    IF s IS NULL THEN
        RETURN NULL;
    END IF;
    RETURN CASE WHEN jsonb_typeof(s::jsonb) IN ('object', 'array') THEN 1 ELSE 0 END;
EXCEPTION WHEN others THEN
    RETURN 0;
END
$$;

-- sql2pg:object Function mssql_compat.isjson_type status=Converted
-- ISJSON(s, type): 1 when s is JSON of that kind (VALUE any, ARRAY, OBJECT, SCALAR a string or
-- number), 0 otherwise, NULL for NULL. Whitespace around the value is allowed.
CREATE OR REPLACE FUNCTION mssql_compat.isjson_type(s text, kind text) RETURNS integer
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    t text;
BEGIN
    IF s IS NULL THEN
        RETURN NULL;
    END IF;
    t := json_typeof(s::json);
    RETURN CASE upper(kind)
        WHEN 'VALUE' THEN 1
        WHEN 'ARRAY' THEN (t = 'array')::int
        WHEN 'OBJECT' THEN (t = 'object')::int
        WHEN 'SCALAR' THEN (t IN ('string', 'number'))::int
        ELSE 0
    END;
EXCEPTION WHEN others THEN
    RETURN 0;
END
$$;

-- sql2pg:object Function mssql_compat.json_modify status=Converted
-- JSON_MODIFY(doc, path, value) over json (not jsonb, which would reorder the members): the
-- path is a text array (lax unless is_strict; append adds to the array there). Members keep
-- their order and a new one goes last, as in SQL Server. In lax mode a NULL value removes an
-- object member (an array element becomes null), a missing member is added at the end, a
-- missing array (append) is created, and a missing parent or index leaves the document as it
-- is; strict mode raises for a missing member and sets null. value is JSON: the caller turns a
-- string into a JSON string.
CREATE OR REPLACE FUNCTION mssql_compat.json_modify(doc json, path text[], new_value json, is_strict boolean, is_append boolean) RETURNS json
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    k text;
    rest text[];
    child json;
BEGIN
    IF doc IS NULL THEN
        RETURN NULL;
    END IF;
    IF cardinality(path) = 0 THEN
        IF NOT is_append THEN
            RETURN coalesce(new_value, 'null'::json);
        END IF;
        IF json_typeof(doc) <> 'array' THEN
            RETURN doc;
        END IF;
        RETURN (SELECT json_agg(x.value ORDER BY x.n) FROM (
            SELECT a.value, a.n FROM json_array_elements(doc) WITH ORDINALITY AS a(value, n)
            UNION ALL SELECT coalesce(new_value, 'null'::json), 9223372036854775807) AS x);
    END IF;
    k := path[1];
    rest := path[2:];
    IF json_typeof(doc) = 'object' THEN
        IF NOT EXISTS (SELECT 1 FROM json_each(doc) AS e WHERE e.key = k) THEN
            IF is_strict THEN
                RAISE EXCEPTION 'JSON_MODIFY: property % cannot be found in strict mode', k;
            END IF;
            IF cardinality(rest) > 0 THEN
                RETURN doc;
            ELSIF is_append THEN
                child := json_build_array(new_value);
            ELSIF new_value IS NULL THEN
                RETURN doc;
            ELSE
                child := new_value;
            END IF;
            RETURN (SELECT json_object_agg(x.key, x.value ORDER BY x.n) FROM (
                SELECT e.key, e.value, e.n FROM json_each(doc) WITH ORDINALITY AS e(key, value, n)
                UNION ALL SELECT k, child, 9223372036854775807) AS x);
        END IF;
        IF cardinality(rest) = 0 AND NOT is_append AND new_value IS NULL AND NOT is_strict THEN
            RETURN coalesce((SELECT json_object_agg(e.key, e.value ORDER BY e.n)
                FROM json_each(doc) WITH ORDINALITY AS e(key, value, n) WHERE e.key <> k), '{}'::json);
        END IF;
        RETURN (SELECT json_object_agg(e.key,
                CASE WHEN e.key = k THEN mssql_compat.json_modify(e.value, rest, new_value, is_strict, is_append) ELSE e.value END ORDER BY e.n)
            FROM json_each(doc) WITH ORDINALITY AS e(key, value, n));
    ELSIF json_typeof(doc) = 'array' AND k ~ '^[0-9]+$' THEN
        IF k::bigint >= json_array_length(doc) THEN
            IF is_strict THEN
                RAISE EXCEPTION 'JSON_MODIFY: index % is out of range in strict mode', k;
            END IF;
            RETURN doc;
        END IF;
        RETURN (SELECT json_agg(CASE WHEN a.n - 1 = k::bigint THEN mssql_compat.json_modify(a.value, rest, new_value, is_strict, is_append) ELSE a.value END ORDER BY a.n)
            FROM json_array_elements(doc) WITH ORDINALITY AS a(value, n));
    END IF;
    RETURN doc;
END
$$;

-- sql2pg:object Function mssql_compat.openjson status=Converted
-- OPENJSON(doc) without WITH: one row per member of an object, or element of an array, in
-- document order. key is the member name or the 0-based index; value is a string's text, any
-- other value as written (numbers keep their form, 1.50 stays 1.50), NULL for null; type is
-- 0 null, 1 string, 2 number, 3 true/false, 4 array, 5 object. A scalar or NULL gives no rows.
CREATE OR REPLACE FUNCTION mssql_compat.openjson(doc json) RETURNS TABLE (key text, value text, type integer)
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT e.k,
       CASE json_typeof(e.v) WHEN 'string' THEN e.v #>> '{}' WHEN 'null' THEN NULL ELSE e.v::text END,
       CASE json_typeof(e.v) WHEN 'null' THEN 0 WHEN 'string' THEN 1 WHEN 'number' THEN 2 WHEN 'boolean' THEN 3 WHEN 'array' THEN 4 ELSE 5 END
FROM (
    SELECT o.key AS k, o.value AS v, o.n
    FROM json_each(CASE WHEN json_typeof(doc) = 'object' THEN doc END) WITH ORDINALITY AS o(key, value, n)
    UNION ALL
    SELECT (a.n - 1)::text, a.value, a.n
    FROM json_array_elements(CASE WHEN json_typeof(doc) = 'array' THEN doc END) WITH ORDINALITY AS a(value, n)
) AS e
ORDER BY e.n
$$;

-- sql2pg: phase Tables

-- sql2pg:object Table public.city status=Converted
CREATE TABLE public.city (
    cityid integer NOT NULL,
    cityname varchar(50) NOT NULL
);

-- sql2pg:object Table public.customer status=Converted
CREATE TABLE public.customer (
    customerid integer NOT NULL,
    customername varchar(100) NOT NULL,
    cityid integer NOT NULL,
    phone varchar(20),
    customfields text
);

-- sql2pg:object Table public.reading status=Converted
CREATE TABLE public.reading (
    rego varchar(20) NOT NULL,
    sensor integer NOT NULL,
    recordedwhen timestamp(6) NOT NULL,
    temperature numeric(18,2) NOT NULL,
    fulldata text NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.recordreadings status=Converted
CREATE PROCEDURE public.recordreadings(data text)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF mssql_compat.isjson(CAST(recordreadings.data AS text)) = 0 THEN
        RAISE EXCEPTION USING MESSAGE = 'Data must be JSON', ERRCODE = 'P0001', DETAIL = 'mssql_error=51000';
    END IF;
    INSERT INTO public.reading (rego, sensor, recordedwhen, temperature, fulldata)
    SELECT rego, sensor, recordedwhen, temperature, fulldata
    FROM (SELECT CASE WHEN json_typeof(e.value #> '{properties,rego}') IN ('object', 'array') THEN NULL ELSE CAST(e.value #> '{properties,rego}' #>> '{}' AS varchar(20)) END AS rego, CASE WHEN json_typeof(e.value #> '{properties,sensor}') IN ('object', 'array') THEN NULL ELSE CAST(e.value #> '{properties,sensor}' #>> '{}' AS integer) END AS sensor, CASE WHEN json_typeof(e.value #> '{properties,when}') IN ('object', 'array') THEN NULL ELSE CAST(e.value #> '{properties,when}' #>> '{}' AS timestamp(6)) END AS recordedwhen, CASE WHEN json_typeof(e.value #> '{properties,temp}') IN ('object', 'array') THEN NULL ELSE CAST(e.value #> '{properties,temp}' #>> '{}' AS numeric(18,2)) END AS temperature, CASE WHEN json_typeof(e.value #> '{}') IN ('object', 'array') THEN CAST(e.value #> '{}' AS text) END AS fulldata FROM json_array_elements(CASE WHEN json_typeof(CAST(recordreadings.data AS json) #> '{Recordings}') = 'array' THEN CAST(recordreadings.data AS json) #> '{Recordings}' ELSE json_build_array(CAST(recordreadings.data AS json) #> '{Recordings}') END) AS e (value)) AS openjson;
END;
$$;

-- sql2pg:object Procedure public.searchcustomers status=Converted
CREATE FUNCTION public.searchcustomers(searchtext varchar(100), rows integer)
RETURNS TABLE ("json_f52e2b61-18a1-11d1-b105-00805f49916b" text)
LANGUAGE plpgsql
STABLE
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY SELECT CAST(CAST(json_build_object('Customers', json_agg(obj ORDER BY first_rn)) AS text) AS text) AS "json_f52e2b61-18a1-11d1-b105-00805f49916b"
        FROM (SELECT min(first_rn) AS first_rn, json_strip_nulls(json_build_object('CustomerId', (array_agg(json_value_1 ORDER BY first_rn))[1], 'CustomerName', (array_agg(json_value_2 ORDER BY first_rn))[1], 'Phone', (array_agg(json_value_4 ORDER BY first_rn))[1], 'ct', json_agg(obj ORDER BY first_rn))) AS obj FROM (SELECT group_1, rn AS first_rn, json_value_1, json_value_2, json_value_4, json_strip_nulls(json_build_object('CityName', json_value_3)) AS obj FROM (SELECT json_value_1, json_value_2, json_value_3, json_value_4, rn, sum(break_1) OVER (ORDER BY rn) AS group_1 FROM (SELECT json_value_1, json_value_2, json_value_3, json_value_4, rn, CASE WHEN rn > 1 AND ROW(CAST(json_value_1 AS text), CAST(json_value_2 AS text), CAST(json_value_4 AS text)) IS NOT DISTINCT FROM ROW(lag(CAST(json_value_1 AS text)) OVER (ORDER BY rn), lag(CAST(json_value_2 AS text)) OVER (ORDER BY rn), lag(CAST(json_value_4 AS text)) OVER (ORDER BY rn)) THEN 0 ELSE 1 END AS break_1 FROM (SELECT c.customerid AS json_value_1, c.customername AS json_value_2, ct.cityname AS json_value_3, c.phone AS json_value_4, row_number() OVER (ORDER BY c.customername NULLS FIRST) AS rn FROM public.customer AS c JOIN public.city AS ct ON c.cityid = ct.cityid WHERE c.customername LIKE '%' || searchcustomers.searchtext || '%' ORDER BY c.customername NULLS FIRST LIMIT searchcustomers.rows) AS json_rows) AS json_breaks) AS json_groups) AS json_level_2 GROUP BY group_1) AS json_level_1;
END;
$$;

-- sql2pg:object Procedure public.tagcustomer status=ConvertedWithWarnings
CREATE PROCEDURE public.tagcustomer(id integer, tag varchar(30), INOUT result_set_1 refcursor DEFAULT NULL, INOUT result_set_2 refcursor DEFAULT NULL)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE public.customer
    SET customfields = CAST(mssql_compat.json_modify(CAST(customfields AS json), CAST('{Tags}' AS text[]), to_json(CAST(tagcustomer.tag AS text)), false, true) AS text)
    WHERE customerid = tagcustomer.id;
    OPEN result_set_1 FOR SELECT CAST(mssql_compat.json_modify(CAST('{"Events": []}' AS json), CAST('{Events}' AS text[]), CAST(CAST(mssql_compat.json_modify(CAST(CAST(mssql_compat.json_modify(CAST('{ }' AS json), CAST('{Event}' AS text[]), to_json(CAST('Ready' AS text)), false, false) AS text) AS json), CAST('{When}' AS text[]), to_json(CAST(CAST(regexp_replace(to_char(localtimestamp, 'YYYY-MM-DD"T"HH24:MI:SS.FF6'), '\.0+$', '') AS varchar(20)) AS text)), false, false) AS text) AS json), false, true) AS text) AS delivery, mssql_compat.isjson_type(CAST(customfields AS text), 'OBJECT') AS isobject
        FROM public.customer
        WHERE customerid = tagcustomer.id;
    OPEN result_set_2 FOR SELECT t.key, t.value, t.type
        FROM public.customer AS c CROSS JOIN LATERAL mssql_compat.openjson(CAST(c.customfields AS json)) AS t
        WHERE c.customerid = tagcustomer.id;
END;
$$;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.city.city_pkey status=Converted
ALTER TABLE public.city ADD CONSTRAINT city_pkey PRIMARY KEY (cityid);

-- sql2pg:object Constraint public.customer.customer_pkey status=Converted
ALTER TABLE public.customer ADD CONSTRAINT customer_pkey PRIMARY KEY (customerid);
