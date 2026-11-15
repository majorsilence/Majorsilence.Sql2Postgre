-- ISJSON(s): 1 when s is a JSON object or array, 0 otherwise, NULL for NULL.
CREATE OR REPLACE FUNCTION {schema}.isjson(s text) RETURNS integer
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
