-- OPENJSON(doc) without WITH: one row per member of an object, or element of an array, in
-- document order. key is the member name or the 0-based index; value is a string's text, any
-- other value as written (numbers keep their form, 1.50 stays 1.50), NULL for null; type is
-- 0 null, 1 string, 2 number, 3 true/false, 4 array, 5 object. A scalar or NULL gives no rows.
CREATE OR REPLACE FUNCTION {schema}.openjson(doc json) RETURNS TABLE (key text, value text, type integer)
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
