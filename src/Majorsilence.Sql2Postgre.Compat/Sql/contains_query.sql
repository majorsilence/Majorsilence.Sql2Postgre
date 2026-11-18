-- CONTAINS and CONTAINSTABLE: SQL Server's search condition rewritten in to_tsquery's syntax and
-- read by to_tsquery in the given configuration, which stems the words and drops stop words as the
-- index did. Parsed at run time, since the condition is often a variable.
--   "word" / word          'word'          "a b c"  a phrase: 'a' <-> 'b' <-> 'c'
--   "pre*"                 'pre':*         (prefix only inside double quotes, as in SQL Server)
--   AND / &   OR / |   AND NOT / &!        &   |   & !
--   NEAR((a, b), n[, TRUE])                a <1> b | … | a <n+1> b (and b … a unless TRUE):
--                                          at most n words between them; MAX, or no n, is AND
--   a NEAR b / a ~ b                       AND (the generic proximity term)
--   FORMSOF(INFLECTIONAL | THESAURUS, a, b), ISABOUT(a WEIGHT(w), b)   a | b
-- A term of stop words only is dropped with its operator. A condition SQL Server rejects (NOT
-- first, OR NOT, two terms with no operator) raises 42601.
CREATE OR REPLACE FUNCTION {schema}.contains_query_term(term text) RETURNS text
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    quoted boolean := left(term, 1) = '"';
    body text := CASE WHEN left(term, 1) = '"' THEN substr(term, 2, length(term) - 2) ELSE term END;
    w text;
    prefix boolean;
    parts text[] := '{}';
BEGIN
    FOREACH w IN ARRAY regexp_split_to_array(btrim(body), '\s+') LOOP
        prefix := quoted AND right(w, 1) = '*';
        w := CASE WHEN prefix THEN rtrim(w, '*') ELSE w END;
        IF w <> '' THEN
            parts := parts || ('''' || replace(replace(w, '\', '\\'), '''', '''''') || '''' || CASE WHEN prefix THEN ':*' ELSE '' END);
        END IF;
    END LOOP;
    -- '*' has no lexeme: to_tsquery drops it as it drops a stop word.
    RETURN '(' || coalesce(nullif(array_to_string(parts, ' <-> '), ''), '''*''') || ')';
END $$;

CREATE OR REPLACE FUNCTION {schema}.contains_query(config regconfig, condition text) RETURNS tsquery
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET client_min_messages = warning AS $$
DECLARE
    tokens text[];
    n integer;
    i integer := 1;
    tok text;
    up text;
    fn text;
    out text := '';
    depth integer := 0;
    want_operand boolean := true;
    terms text[];
    distance integer;
    ordered boolean;
    k integer;
    d integer;
BEGIN
    IF condition IS NULL THEN
        RETURN NULL;
    END IF;
    IF (length(condition) - length(replace(condition, '"', ''))) % 2 = 1 THEN
        RAISE EXCEPTION 'Syntax error near ''"'' in the full-text search condition ''%''.', condition USING ERRCODE = '42601';
    END IF;
    SELECT array_agg(m[1] ORDER BY ord) INTO tokens
    FROM regexp_matches(condition, '("[^"]*"|&!|[&|~(),]|[^\s&|~(),"]+)', 'g') WITH ORDINALITY AS r(m, ord);
    n := coalesce(cardinality(tokens), 0);
    WHILE i <= n LOOP
        tok := tokens[i];
        up := upper(tok);
        IF want_operand THEN
            IF tok = '(' THEN
                out := out || '(';
                depth := depth + 1;
                i := i + 1;
                CONTINUE;
            ELSIF up IN ('NEAR', 'FORMSOF', 'ISABOUT') AND tokens[i + 1] = '(' THEN
                fn := up;
                i := i + 2;
                terms := '{}';
                distance := NULL;
                ordered := false;
                IF fn = 'FORMSOF' THEN
                    -- FORMSOF(INFLECTIONAL | THESAURUS, …): the configuration stems; there is no thesaurus.
                    i := i + 2;
                END IF;
                IF fn = 'NEAR' AND tokens[i] = '(' THEN
                    i := i + 1;
                    WHILE i <= n AND tokens[i] <> ')' LOOP
                        IF tokens[i] <> ',' THEN
                            terms := terms || {schema}.contains_query_term(tokens[i]);
                        END IF;
                        i := i + 1;
                    END LOOP;
                    i := i + 1;
                    IF tokens[i] = ',' THEN
                        distance := CASE WHEN upper(tokens[i + 1]) = 'MAX' THEN NULL ELSE tokens[i + 1]::integer END;
                        i := i + 2;
                        IF tokens[i] = ',' THEN
                            ordered := upper(tokens[i + 1]) = 'TRUE';
                            i := i + 2;
                        END IF;
                    END IF;
                ELSE
                    WHILE i <= n AND tokens[i] <> ')' LOOP
                        IF upper(tokens[i]) = 'WEIGHT' AND tokens[i + 1] = '(' THEN
                            i := i + 4;
                            CONTINUE;
                        END IF;
                        IF tokens[i] <> ',' THEN
                            terms := terms || {schema}.contains_query_term(tokens[i]);
                        END IF;
                        i := i + 1;
                    END LOOP;
                END IF;
                IF tokens[i] IS DISTINCT FROM ')' OR cardinality(terms) = 0 THEN
                    RAISE EXCEPTION 'Syntax error near ''%'' in the full-text search condition ''%''.', fn, condition USING ERRCODE = '42601';
                END IF;
                IF fn <> 'NEAR' THEN
                    out := out || '(' || array_to_string(terms, ' | ') || ')';
                ELSIF distance IS NULL OR distance > 100 THEN
                    out := out || '(' || array_to_string(terms, ' & ') || ')';
                ELSE
                    -- Each neighbouring pair within distance + 1 positions.
                    FOR k IN 1 .. cardinality(terms) - 1 LOOP
                        out := out || CASE WHEN k > 1 THEN ' & ' ELSE '' END || '(';
                        FOR d IN 1 .. distance + 1 LOOP
                            out := out || CASE WHEN d > 1 THEN ' | ' ELSE '' END || terms[k] || ' <' || d || '> ' || terms[k + 1];
                            IF NOT ordered THEN
                                out := out || ' | ' || terms[k + 1] || ' <' || d || '> ' || terms[k];
                            END IF;
                        END LOOP;
                        out := out || ')';
                    END LOOP;
                    out := '(' || out || ')';
                END IF;
            ELSIF up IN ('AND', 'OR', 'NOT') OR tok IN ('&', '|', '&!', '~', ')', ',') THEN
                RAISE EXCEPTION 'Syntax error near ''%'' in the full-text search condition ''%''.', tok, condition USING ERRCODE = '42601';
            ELSE
                out := out || {schema}.contains_query_term(tok);
            END IF;
            want_operand := false;
        ELSIF tok = ')' AND depth > 0 THEN
            out := out || ')';
            depth := depth - 1;
        ELSIF (up = 'AND' AND upper(tokens[i + 1]) = 'NOT') OR tok = '&!' OR (tok = '&' AND upper(tokens[i + 1]) = 'NOT') THEN
            out := out || ' & !';
            i := i + CASE WHEN tok = '&!' THEN 0 ELSE 1 END;
            want_operand := true;
        ELSIF up IN ('AND', 'NEAR') OR tok IN ('&', '~') THEN
            out := out || ' & ';
            want_operand := true;
        ELSIF up = 'OR' OR tok = '|' THEN
            out := out || ' | ';
            want_operand := true;
        ELSE
            RAISE EXCEPTION 'Syntax error near ''%'' in the full-text search condition ''%''.', tok, condition USING ERRCODE = '42601';
        END IF;
        i := i + 1;
    END LOOP;
    IF want_operand OR depth <> 0 THEN
        RAISE EXCEPTION 'Syntax error in the full-text search condition ''%''.', condition USING ERRCODE = '42601';
    END IF;
    RETURN to_tsquery(config, out);
END $$;
