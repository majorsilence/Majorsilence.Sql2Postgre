SELECT product_id
FROM shop.product;

DO $$
#variable_conflict use_column
DECLARE
    v_p integer := 1;
    sql2pg_row record;
BEGIN
    FOR sql2pg_row IN SELECT product_id AS sql2pg_c1
        FROM shop.product
        WHERE product_id = 1
    LOOP
        v_p := sql2pg_row.sql2pg_c1;
    END LOOP;
    RAISE NOTICE '%', COALESCE(CAST(v_p AS varchar(4000)), '');
END;
$$;

DELETE FROM shop.product
WHERE parent_id = 5;
