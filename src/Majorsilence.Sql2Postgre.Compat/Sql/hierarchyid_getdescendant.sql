-- hierarchyid GetDescendant(child1, child2) over the text form: a child of h after child1 and
-- before child2 (either may be NULL). Labels are sequences of integers (1, 1.1, -1): after a
-- label comes its first number plus one, before it its first number minus one, and between two
-- labels the shortest label that sorts between them.
CREATE OR REPLACE FUNCTION {schema}.hierarchyid_getdescendant(h text, child1 text, child2 text) RETURNS text
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    a bigint[];
    b bigint[];
    result bigint[] := '{}';
    i int := 1;
BEGIN
    IF h IS NULL THEN
        RETURN NULL;
    END IF;
    IF child1 IS NOT NULL THEN
        a := string_to_array(split_part(rtrim(child1, '/'), '/', -1), '.')::bigint[];
    END IF;
    IF child2 IS NOT NULL THEN
        b := string_to_array(split_part(rtrim(child2, '/'), '/', -1), '.')::bigint[];
    END IF;
    IF a IS NULL AND b IS NULL THEN
        result := ARRAY[1];
    ELSIF b IS NULL THEN
        result := ARRAY[a[1] + 1];
    ELSIF a IS NULL THEN
        result := ARRAY[b[1] - 1];
    ELSE
        IF a >= b THEN
            RAISE EXCEPTION 'GetDescendant: child1 (%) must be less than child2 (%)', child1, child2;
        END IF;
        -- Copy the common prefix, then step between the first numbers that differ.
        LOOP
            IF i > cardinality(a) THEN
                -- a is a prefix of b: anything after a and before b's next number.
                result := result || (b[i] - 1);
                IF result >= b THEN
                    result := result[1:cardinality(result) - 1] || b[i] || -1;
                END IF;
                EXIT;
            ELSIF b[i] - a[i] >= 2 THEN
                result := result || (a[i] + 1);
                EXIT;
            ELSIF b[i] - a[i] = 1 THEN
                IF i = cardinality(a) THEN
                    result := result || a[i] || 1;
                ELSE
                    result := result || a[i] || (a[i + 1] + 1);
                END IF;
                EXIT;
            END IF;
            result := result || a[i];
            i := i + 1;
        END LOOP;
    END IF;
    RETURN h || array_to_string(result, '.') || '/';
END
$$;
