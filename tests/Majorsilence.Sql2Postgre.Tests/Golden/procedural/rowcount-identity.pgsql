DO $$
#variable_conflict use_column
DECLARE
    v_id bigint;
    v_rows integer;
    sql2pg_rowcount integer;
    sql2pg_identity numeric(38,0);
BEGIN
    UPDATE shop.customer
    SET is_active = false
    WHERE email IS NULL;
    GET DIAGNOSTICS sql2pg_rowcount = ROW_COUNT;
    IF sql2pg_rowcount = 0 THEN
        RAISE NOTICE '%', 'nothing to do';
        sql2pg_rowcount := 0;
    END IF;
    INSERT INTO shop."order" (customer_id, order_date, total)
    VALUES (100, '2024-01-02', 10)
    RETURNING order_id
    INTO sql2pg_identity;
    GET DIAGNOSTICS sql2pg_rowcount = ROW_COUNT;
    v_id := CAST(trunc(sql2pg_identity) AS bigint);
    sql2pg_rowcount := 1;
    WITH sql2pg_inserted AS (INSERT INTO shop."order" (customer_id, order_date, total)
    SELECT customer_id, '2024-01-03', 0
    FROM shop.customer
    WHERE is_active = true
    RETURNING order_id)
    SELECT COALESCE(max(order_id), sql2pg_identity), CAST(count(*) AS integer)
    INTO sql2pg_identity, sql2pg_rowcount
    FROM sql2pg_inserted;
    v_id := CAST(trunc(sql2pg_identity) AS bigint);
    v_rows := sql2pg_rowcount;
    sql2pg_rowcount := 1;
    DELETE FROM shop."order"
    WHERE order_id = v_id;
    GET DIAGNOSTICS sql2pg_rowcount = ROW_COUNT;
    IF 0 <> 0 THEN
        RAISE NOTICE '%', 'failed';
        sql2pg_rowcount := 0;
    END IF;
END;
$$;
