-- TRY_CAST(s AS uuid): the value, or NULL when s does not convert.
CREATE OR REPLACE FUNCTION {schema}.try_cast_uuid(s text) RETURNS uuid
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    RETURN s::uuid;
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
