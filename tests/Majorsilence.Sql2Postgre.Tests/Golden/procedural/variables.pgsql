DO $$
#variable_conflict use_column
DECLARE
    v_count integer := 3;
    v_name varchar(50) := 'shop';
    v_price numeric(10,2);
    v_whole integer;
    v_short varchar(3);
BEGIN
    v_price := CAST(12.75 AS numeric(10,2));
    v_count := v_count + 2;
    v_name := CAST(v_name || ' floor' AS varchar(50));
    v_whole := CAST(trunc(v_price) AS integer);
    v_short := CAST(v_name AS varchar(3));
    v_count := v_count * 2;
    RAISE NOTICE '%', COALESCE(CAST(v_count AS varchar(4000)), '');
END;
$$;
