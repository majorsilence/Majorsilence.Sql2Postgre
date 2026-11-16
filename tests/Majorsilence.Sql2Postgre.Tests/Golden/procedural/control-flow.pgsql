DO $$
#variable_conflict use_column
DECLARE
    v_i integer := 0;
    v_odd integer := 0;
BEGIN
    WHILE v_i < 10 LOOP
        v_i := v_i + 1;
        IF v_i % 2 = 0 THEN
            CONTINUE;
        END IF;
        v_odd := v_odd + 1;
        IF v_odd >= 4 THEN
            EXIT;
        END IF;
    END LOOP;
    IF v_odd = 4 THEN
        RAISE NOTICE '%', 'four';
    ELSIF v_odd > 4 THEN
        RAISE NOTICE '%', 'more';
        RETURN;
    ELSE
        RAISE NOTICE '%', 'fewer';
    END IF;
END;
$$;
