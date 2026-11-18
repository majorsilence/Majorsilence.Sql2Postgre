-- ISJSON(s, type): 1 when s is JSON of that kind (VALUE any, ARRAY, OBJECT, SCALAR a string or
-- number), 0 otherwise, NULL for NULL. Whitespace around the value is allowed.
CREATE OR REPLACE FUNCTION {schema}.isjson_type(s text, kind text) RETURNS integer
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
