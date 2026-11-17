-- hierarchyid GetAncestor(n) over the text form: the node n levels up; the node itself for 0, the
-- root (/) at the top, and NULL above the root or for a negative n.
CREATE OR REPLACE FUNCTION {schema}.hierarchyid_getancestor(h text, n bigint) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE
    WHEN h IS NULL OR n IS NULL OR n < 0 THEN NULL
    WHEN n > cardinality(levels) THEN NULL
    WHEN n = cardinality(levels) THEN '/'
    ELSE '/' || array_to_string(levels[1:cardinality(levels) - n], '/') || '/'
END
FROM (SELECT string_to_array(btrim(h, '/'), '/') AS levels) AS l
$$;
