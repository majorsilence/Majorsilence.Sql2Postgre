-- TRY_CAST(s AS numeric): the value, or NULL when s does not convert.
CREATE OR REPLACE FUNCTION {schema}.try_cast_numeric(s text) RETURNS numeric
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    RETURN s::numeric;
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
