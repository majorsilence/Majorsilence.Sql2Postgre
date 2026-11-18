-- FREETEXT and FREETEXTTABLE: a row matches when it has any of the words, stemmed in the given
-- configuration (stop words dropped), as SQL Server matches any of them in their inflected forms.
-- Quotes are ignored, as in FREETEXT. No words left matches nothing.
CREATE OR REPLACE FUNCTION {schema}.freetext_query(config regconfig, words text) RETURNS tsquery
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
