-- hierarchyid IsDescendantOf(p) over the text form: true when p is h or one of its ancestors
-- (levels end in /, so a prefix of the text is a prefix of the path).
CREATE OR REPLACE FUNCTION {schema}.hierarchyid_isdescendantof(h text, p text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT left(h, length(p)) = p
$$;
