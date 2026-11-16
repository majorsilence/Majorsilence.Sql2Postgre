DO $$
#variable_conflict use_column
DECLARE
    v_flag boolean := true;
    v_n integer := 7;
    v_when date := CAST('2024-02-29' AS date);
BEGIN
    RAISE NOTICE '%', COALESCE(CAST(CAST(v_flag AS integer) AS varchar(4000)), '');
    RAISE NOTICE '%', COALESCE(CAST(v_n AS varchar(4000)), '');
    RAISE NOTICE '%', COALESCE('n is ' || CAST(v_n AS varchar(10)), '');
    RAISE NOTICE '%', COALESCE(NULL, '');
    RAISE NOTICE 'progress % of %', v_n, 'ten';
    RAISE EXCEPTION 'padded %, 100%% done', v_n;
    RAISE NOTICE '%', 'after';
    /* sql2pg:untranslated S2P5008
    RAISERROR(50001, 16, 1);
    */
    RAISE EXCEPTION USING MESSAGE = 'custom failure', ERRCODE = 'P0001', DETAIL = 'mssql_error=50002';
END;
$$;
