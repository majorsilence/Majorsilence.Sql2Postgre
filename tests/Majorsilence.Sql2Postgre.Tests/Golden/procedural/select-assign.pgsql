DO $$
#variable_conflict use_column
DECLARE
    v_name varchar(100);
    v_email varchar(200);
    v_count integer;
    v_total numeric(19,4);
    v_list text := '';
    sql2pg_row record;
BEGIN
    v_count := 1;
    v_total := 0;
    SELECT count(*), sum(total)
    INTO v_count, v_total
    FROM shop."order"
    WHERE customer_id = 100;
    SELECT email
    INTO v_email
    FROM shop.customer
    WHERE customer_id = 105;
    SELECT name
    INTO v_name
    FROM shop.customer
    WHERE customer_id = 110;
    FOR sql2pg_row IN SELECT name AS sql2pg_c1, email AS sql2pg_c2
        FROM shop.customer
        WHERE is_active = true
        ORDER BY created_at NULLS FIRST
    LOOP
        v_name := sql2pg_row.sql2pg_c1;
        v_email := sql2pg_row.sql2pg_c2;
    END LOOP;
    FOR sql2pg_row IN SELECT total AS sql2pg_c1
        FROM shop."order"
        ORDER BY order_date DESC NULLS LAST
        LIMIT 1
    LOOP
        v_total := sql2pg_row.sql2pg_c1;
    END LOOP;
    FOR sql2pg_row IN SELECT email AS sql2pg_c1
        FROM shop.customer
        WHERE name LIKE 'A%'
    LOOP
        v_email := sql2pg_row.sql2pg_c1;
    END LOOP;
    FOR sql2pg_row IN SELECT name AS sql2pg_c1
        FROM shop.product
        WHERE parent_id IS NULL
        ORDER BY product_id NULLS FIRST
    LOOP
        v_list := v_list || sql2pg_row.sql2pg_c1 || ', ';
    END LOOP;
    IF v_total > 0 THEN
        v_count := v_count + 1;
    END IF;
END;
$$;
