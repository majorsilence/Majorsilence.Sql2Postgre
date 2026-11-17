-- The sort key of a hierarchyid's text form: each level's numbers (a label may be dotted, 1.2,
-- or negative), each level ended by a value below any label. Comparing keys as arrays orders
-- nodes depth-first, a parent before its children, as SQL Server orders hierarchyid.
CREATE OR REPLACE FUNCTION {schema}.hierarchyid_key(h text) RETURNS bigint[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE WHEN h IS NULL THEN NULL ELSE coalesce((
    SELECT array_agg(part.v ORDER BY l.depth, part.pos)
    FROM unnest(string_to_array(btrim(h, '/'), '/')) WITH ORDINALITY AS l(label, depth)
    CROSS JOIN LATERAL (
        SELECT c.v::bigint AS v, c.pos FROM unnest(string_to_array(l.label, '.')) WITH ORDINALITY AS c(v, pos)
        UNION ALL
        SELECT '-9223372036854775808'::bigint, 2147483647
    ) AS part), '{}'::bigint[]) END
$$;
