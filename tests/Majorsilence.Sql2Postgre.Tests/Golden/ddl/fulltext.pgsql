-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.contains_query status=Converted
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
CREATE OR REPLACE FUNCTION mssql_compat.contains_query_term(term text) RETURNS text
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

CREATE OR REPLACE FUNCTION mssql_compat.contains_query(config regconfig, condition text) RETURNS tsquery
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
                            terms := terms || mssql_compat.contains_query_term(tokens[i]);
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
                            terms := terms || mssql_compat.contains_query_term(tokens[i]);
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
                out := out || mssql_compat.contains_query_term(tok);
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

-- sql2pg:object Function mssql_compat.freetext_query status=Converted
-- FREETEXT and FREETEXTTABLE: a row matches when it has any of the words, stemmed in the given
-- configuration (stop words dropped), as SQL Server matches any of them in their inflected forms.
-- Quotes are ignored, as in FREETEXT. No words left matches nothing.
CREATE OR REPLACE FUNCTION mssql_compat.freetext_query(config regconfig, words text) RETURNS tsquery
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET client_min_messages = warning AS $$
DECLARE
    alternatives text;
BEGIN
    IF words IS NULL THEN
        RETURN NULL;
    END IF;
    SELECT string_agg('''' || replace(replace(v.lexeme, '\', '\\'), '''', '''''') || '''', ' | ' ORDER BY v.lexeme)
    INTO alternatives
    FROM unnest(to_tsvector(config, words)) AS v;
    RETURN coalesce(alternatives, '')::tsquery;
END $$;

-- sql2pg:object Function mssql_compat.fulltext_config status=Converted
-- The text search configuration for a SQL Server full-text language (an LCID), for a LANGUAGE
-- given at run time: the built-in configuration for that language, else simple (lower-cased
-- words, no stemming, no stop words), which is also the neutral language 0. The converter maps a
-- constant LANGUAGE with the same table.
CREATE OR REPLACE FUNCTION mssql_compat.fulltext_config(lcid integer) RETURNS regconfig
LANGUAGE sql IMMUTABLE PARALLEL SAFE
RETURN CASE lcid
    WHEN 1025 THEN 'pg_catalog.arabic'
    WHEN 1027 THEN 'pg_catalog.catalan'
    WHEN 1030 THEN 'pg_catalog.danish'
    WHEN 1031 THEN 'pg_catalog.german'
    WHEN 1032 THEN 'pg_catalog.greek'
    WHEN 1033 THEN 'pg_catalog.english'
    WHEN 1036 THEN 'pg_catalog.french'
    WHEN 1040 THEN 'pg_catalog.italian'
    WHEN 1043 THEN 'pg_catalog.dutch'
    WHEN 1044 THEN 'pg_catalog.norwegian'
    WHEN 1046 THEN 'pg_catalog.portuguese'
    WHEN 1048 THEN 'pg_catalog.romanian'
    WHEN 1049 THEN 'pg_catalog.russian'
    WHEN 1053 THEN 'pg_catalog.swedish'
    WHEN 1055 THEN 'pg_catalog.turkish'
    WHEN 1057 THEN 'pg_catalog.indonesian'
    WHEN 1063 THEN 'pg_catalog.lithuanian'
    WHEN 1081 THEN 'pg_catalog.hindi'
    WHEN 1097 THEN 'pg_catalog.tamil'
    WHEN 2057 THEN 'pg_catalog.english'
    WHEN 2070 THEN 'pg_catalog.portuguese'
    WHEN 2074 THEN 'pg_catalog.serbian'
    WHEN 3082 THEN 'pg_catalog.spanish'
    WHEN 3098 THEN 'pg_catalog.serbian'
    ELSE 'pg_catalog.simple'
END::regconfig;

-- sql2pg:object Function mssql_compat.fulltext_lcid status=Converted
-- SERVERPROPERTY('LCID'): the server's language, read from the database's default text search
-- configuration (default_text_search_config, which initdb sets from the locale), as the LCID
-- fulltext_config maps back to it: 1033 for english. 0 (neutral) for simple and the others.
CREATE OR REPLACE FUNCTION mssql_compat.fulltext_lcid() RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE
RETURN CASE replace(current_setting('default_text_search_config'), 'pg_catalog.', '')
    WHEN 'arabic' THEN 1025
    WHEN 'catalan' THEN 1027
    WHEN 'danish' THEN 1030
    WHEN 'german' THEN 1031
    WHEN 'greek' THEN 1032
    WHEN 'english' THEN 1033
    WHEN 'french' THEN 1036
    WHEN 'italian' THEN 1040
    WHEN 'dutch' THEN 1043
    WHEN 'norwegian' THEN 1044
    WHEN 'portuguese' THEN 2070
    WHEN 'romanian' THEN 1048
    WHEN 'russian' THEN 1049
    WHEN 'swedish' THEN 1053
    WHEN 'turkish' THEN 1055
    WHEN 'indonesian' THEN 1057
    WHEN 'lithuanian' THEN 1063
    WHEN 'hindi' THEN 1081
    WHEN 'tamil' THEN 1097
    WHEN 'serbian' THEN 2074
    WHEN 'spanish' THEN 3082
    ELSE 0
END;

-- sql2pg: phase Tables

-- sql2pg:object Table public.doc status=Converted
CREATE TABLE public.doc (
    id integer NOT NULL,
    title varchar(200),
    body text,
    notes xml,
    content bytea,
    extension varchar(8)
);

-- sql2pg:object Table public.memo status=Converted
CREATE TABLE public.memo (
    memoid integer NOT NULL,
    txt varchar(400),
    summary varchar(200)
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object View public.appledocs status=ConvertedWithWarnings
CREATE VIEW public.appledocs AS
SELECT id, title
FROM public.doc
WHERE to_tsvector('english', doc.title) @@ mssql_compat.contains_query('english', '"apple*" AND NOT pie') OR to_tsvector('english', doc.body) @@ mssql_compat.contains_query('english', '"apple*" AND NOT pie') OR to_tsvector('english', CAST(doc.notes AS text)) @@ mssql_compat.contains_query('english', '"apple*" AND NOT pie');

-- sql2pg:object Procedure public.search status=ConvertedWithWarnings
CREATE PROCEDURE public.search(q varchar(400), lang integer DEFAULT 0, top integer DEFAULT 10, inflect boolean DEFAULT false, INOUT result_set_1 refcursor DEFAULT NULL, INOUT result_set_2 refcursor DEFAULT NULL, INOUT result_set_3 refcursor DEFAULT NULL, INOUT result_set_4 refcursor DEFAULT NULL, INOUT result_set_5 refcursor DEFAULT NULL)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF search.inflect = true THEN
        search.q := CAST('FORMSOF(INFLECTIONAL, "' || search.q || '")' AS varchar(400));
    END IF;
    OPEN result_set_1 FOR SELECT d.id, k.rank
        FROM public.doc AS d JOIN (SELECT id AS key, CAST(ts_rank(COALESCE(to_tsvector('english', title), CAST('' AS tsvector)) || COALESCE(to_tsvector('english', body), CAST('' AS tsvector)), (SELECT mssql_compat.contains_query(mssql_compat.fulltext_config(search.lang), search.q))) * 1000 AS integer) AS rank FROM public.doc WHERE to_tsvector('english', title) @@ (SELECT mssql_compat.contains_query(mssql_compat.fulltext_config(search.lang), search.q)) OR to_tsvector('english', body) @@ (SELECT mssql_compat.contains_query(mssql_compat.fulltext_config(search.lang), search.q)) ORDER BY rank DESC LIMIT search.top) AS k ON d.id = k.key
        ORDER BY k.rank DESC NULLS LAST;
    OPEN result_set_2 FOR SELECT id
        FROM public.doc
        WHERE to_tsvector('english', doc.body) @@ (SELECT mssql_compat.freetext_query('english', search.q)) OR to_tsvector('english', doc.title) @@ mssql_compat.contains_query('english', 'NEAR((red, apple), 2)');
    OPEN result_set_3 FOR SELECT k.key, k.rank
        FROM (SELECT id AS key, CAST(ts_rank(COALESCE(to_tsvector('english', title), CAST('' AS tsvector)) || COALESCE(to_tsvector('english', body), CAST('' AS tsvector)) || COALESCE(to_tsvector('english', CAST(notes AS text)), CAST('' AS tsvector)), (SELECT mssql_compat.freetext_query('english', search.q))) * 1000 AS integer) AS rank FROM public.doc WHERE to_tsvector('english', title) @@ (SELECT mssql_compat.freetext_query('english', search.q)) OR to_tsvector('english', body) @@ (SELECT mssql_compat.freetext_query('english', search.q)) OR to_tsvector('english', CAST(notes AS text)) @@ (SELECT mssql_compat.freetext_query('english', search.q))) AS k;
    OPEN result_set_4 FOR SELECT m.memoid
        FROM public.memo AS m
        WHERE to_tsvector('english', m.summary) @@ (SELECT mssql_compat.contains_query('english', search.q));
    OPEN result_set_5 FOR SELECT CAST(mssql_compat.fulltext_lcid() AS integer) AS lcid;
END;
$$;

-- sql2pg: phase Indexes

-- sql2pg:object Index public.fulltext_doc status=ConvertedWithWarnings
CREATE INDEX fulltext_doc ON public.doc USING gin (to_tsvector('english', title), to_tsvector('english', body), to_tsvector('english', CAST(notes AS text)));

-- sql2pg:object Index public.fulltext_memo status=ConvertedWithWarnings
CREATE INDEX fulltext_memo ON public.memo USING gin (to_tsvector('simple', txt), to_tsvector('english', summary));

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.doc.pk_doc status=Converted
ALTER TABLE public.doc ADD CONSTRAINT pk_doc PRIMARY KEY (id);

-- sql2pg:object Constraint public.memo.memo_pkey status=Converted
ALTER TABLE public.memo ADD CONSTRAINT memo_pkey PRIMARY KEY (memoid);
