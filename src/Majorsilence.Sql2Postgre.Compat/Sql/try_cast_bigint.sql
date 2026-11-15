-- TRY_CAST(s AS bigint): the value, or NULL when s does not convert. An empty (or all-blank)
-- string converts to 0, as SQL Server converts it.
CREATE OR REPLACE FUNCTION {schema}.try_cast_bigint(s text) RETURNS bigint
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    IF btrim(s) = '' THEN
        RETURN 0;
    END IF;
    RETURN s::bigint;
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
