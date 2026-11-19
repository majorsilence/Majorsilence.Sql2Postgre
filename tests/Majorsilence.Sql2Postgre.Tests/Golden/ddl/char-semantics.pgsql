-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.char_like status=Converted
-- LIKE on a char(n) value with a non-Unicode pattern, as SQL Server matches it: the value's
-- trailing spaces may be left off, any number of them ('ab    ' matches 'ab', 'ab_' and '______',
-- not '_______'). value is the padded value as text; the pattern's own spaces all count.
CREATE OR REPLACE FUNCTION mssql_compat.char_like(value text, pattern text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE
RETURN (SELECT bool_or(left(value, length(value) - k) LIKE pattern)
        FROM generate_series(0, length(value) - length(rtrim(value, ' '))) AS k);

-- sql2pg: phase Tables

-- sql2pg:object Table public.title status=Converted
CREATE TABLE public.title (
    titleid char(6) NOT NULL,
    type char(12) NOT NULL,
    pubid char(4),
    code char(5),
    note varchar(20)
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.find status=Converted
CREATE FUNCTION public.find(pattern varchar(20))
RETURNS TABLE (titleid char(6))
LANGUAGE plpgsql
STABLE
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY SELECT CAST(titleid AS bpchar) AS titleid
        FROM public.title
        WHERE type LIKE 'bus%' OR CAST(type AS text) LIKE '%cook' OR mssql_compat.char_like(rpad(type, 12), 'trad_') OR mssql_compat.char_like(rpad(type, 12), find.pattern) OR code LIKE '%y';
END;
$$;

-- sql2pg:object View public.titlelabels status=Converted
CREATE VIEW public.titlelabels AS
SELECT titleid, CASE WHEN pubid IS NULL THEN 'ALL' ELSE rpad(pubid, 4) END AS pub, COALESCE(note, rpad(type, 12)) AS noteortype, rpad(type, 12) || '/' || rpad(titleid, 6) AS path, CAST(rpad(type, 12) AS varchar(20)) AS typetext, upper(rpad(type, 12)) AS uppertype, left(rpad(type, 12), 3) AS prefix, length(rtrim(type)) AS typelength
FROM public.title
WHERE type = 'business' OR type || '' = 'mod_cook' OR CASE WHEN pubid IS NULL THEN 'ALL' ELSE pubid END IN ('ALL', '0877');

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.title.title_pkey status=Converted
ALTER TABLE public.title ADD CONSTRAINT title_pkey PRIMARY KEY (titleid);
