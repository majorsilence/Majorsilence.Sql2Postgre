DO $$
#variable_conflict use_column
BEGIN
    RAISE NOTICE '%', COALESCE(CAST(CAST(extract(dow FROM CAST('2026-11-20' AS timestamp)) AS integer) + 1 AS varchar(4000)), '');
END;
$$;
