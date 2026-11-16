DO $$
#variable_conflict use_column
DECLARE
    v_wait varchar(12) := '00:00:00.250';
BEGIN
    PERFORM pg_sleep(1.5);
    PERFORM pg_sleep(extract(epoch FROM CAST(v_wait AS interval)));
    /* sql2pg:untranslated S2P5009
    SELECT name FROM shop.product;
    */
    /* sql2pg:untranslated S2P5027
    EXEC shop.do_something;
    */
    /* sql2pg:untranslated S2P5005
    retry:
    */
    /* sql2pg:untranslated S2P5005
    GOTO retry;
    */
END;
$$;
