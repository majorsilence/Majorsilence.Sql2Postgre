-- JSON_MODIFY(doc, path, value) over json (not jsonb, which would reorder the members): the
-- path is a text array (lax unless is_strict; append adds to the array there). Members keep
-- their order and a new one goes last, as in SQL Server. In lax mode a NULL value removes an
-- object member (an array element becomes null), a missing member is added at the end, a
-- missing array (append) is created, and a missing parent or index leaves the document as it
-- is; strict mode raises for a missing member and sets null. value is JSON: the caller turns a
-- string into a JSON string.
CREATE OR REPLACE FUNCTION {schema}.json_modify(doc json, path text[], new_value json, is_strict boolean, is_append boolean) RETURNS json
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
                CASE WHEN e.key = k THEN {schema}.json_modify(e.value, rest, new_value, is_strict, is_append) ELSE e.value END ORDER BY e.n)
            FROM json_each(doc) WITH ORDINALITY AS e(key, value, n));
    ELSIF json_typeof(doc) = 'array' AND k ~ '^[0-9]+$' THEN
        IF k::bigint >= json_array_length(doc) THEN
            IF is_strict THEN
                RAISE EXCEPTION 'JSON_MODIFY: index % is out of range in strict mode', k;
            END IF;
            RETURN doc;
        END IF;
        RETURN (SELECT json_agg(CASE WHEN a.n - 1 = k::bigint THEN {schema}.json_modify(a.value, rest, new_value, is_strict, is_append) ELSE a.value END ORDER BY a.n)
            FROM json_array_elements(doc) WITH ORDINALITY AS a(value, n));
    END IF;
    RETURN doc;
END
$$;
