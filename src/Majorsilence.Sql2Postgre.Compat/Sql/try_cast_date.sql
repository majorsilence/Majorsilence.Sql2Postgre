-- TRY_CAST(s AS date): the value, or NULL when s does not convert. An empty (or all-blank)
-- string converts to '1900-01-01', as SQL Server converts it.
CREATE OR REPLACE FUNCTION {schema}.try_cast_date(s text) RETURNS date
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    IF btrim(s) = '' THEN
        RETURN '1900-01-01';
    END IF;
    RETURN s::date;
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
