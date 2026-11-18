-- The text search configuration for a SQL Server full-text language (an LCID), for a LANGUAGE
-- given at run time: the built-in configuration for that language, else simple (lower-cased
-- words, no stemming, no stop words), which is also the neutral language 0. The converter maps a
-- constant LANGUAGE with the same table.
CREATE OR REPLACE FUNCTION {schema}.fulltext_config(lcid integer) RETURNS regconfig
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
