-- SERVERPROPERTY('LCID'): the server's language, read from the database's default text search
-- configuration (default_text_search_config, which initdb sets from the locale), as the LCID
-- fulltext_config maps back to it: 1033 for english. 0 (neutral) for simple and the others.
CREATE OR REPLACE FUNCTION {schema}.fulltext_lcid() RETURNS integer
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
