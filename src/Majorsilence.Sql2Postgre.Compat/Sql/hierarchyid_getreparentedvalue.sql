-- hierarchyid GetReparentedValue(old_root, new_root) over the text form: h moved from under
-- old_root to under new_root (old_root itself becomes new_root). NULL when h is not under old_root.
CREATE OR REPLACE FUNCTION {schema}.hierarchyid_getreparentedvalue(h text, old_root text, new_root text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE WHEN left(h, length(old_root)) = old_root THEN new_root || substr(h, length(old_root) + 1) END
$$;
