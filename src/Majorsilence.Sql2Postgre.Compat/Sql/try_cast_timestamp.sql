-- TRY_CAST(s AS timestamp): the value, or NULL when s does not convert. An empty (or all-blank)
-- string converts to '1900-01-01 00:00:00', as SQL Server converts it.
CREATE OR REPLACE FUNCTION {schema}.try_cast_timestamp(s text) RETURNS timestamp
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    IF btrim(s) = '' THEN
        RETURN '1900-01-01 00:00:00';
    END IF;
    RETURN s::timestamp;
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
