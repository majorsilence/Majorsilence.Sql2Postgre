-- ISDATE(s): 1 when s reads as a date or datetime, else 0 (NULL gives 0).
CREATE OR REPLACE FUNCTION {schema}.isdate(s text) RETURNS integer
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $$
BEGIN
    IF s IS NULL OR btrim(s) = '' THEN
        RETURN 0;
    END IF;
    PERFORM s::timestamp;
    RETURN 1;
EXCEPTION WHEN others THEN
    RETURN 0;
END
$$;
