-- hierarchyid GetLevel() over the text form (/1/2/): the number of levels below the root (/ is 0).
CREATE OR REPLACE FUNCTION {schema}.hierarchyid_getlevel(h text) RETURNS smallint
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT (length(h) - length(replace(h, '/', '')) - 1)::smallint
$$;
