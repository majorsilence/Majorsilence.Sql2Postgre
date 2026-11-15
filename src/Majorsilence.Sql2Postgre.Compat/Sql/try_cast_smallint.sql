-- TRY_CAST(s AS smallint): the value, or NULL when s does not convert. An empty (or all-blank)
-- string converts to 0, as SQL Server converts it.
CREATE OR REPLACE FUNCTION {schema}.try_cast_smallint(s text) RETURNS smallint
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    IF btrim(s) = '' THEN
        RETURN 0;
    END IF;
    RETURN s::smallint;
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
