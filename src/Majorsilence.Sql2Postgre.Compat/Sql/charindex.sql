-- CHARINDEX(needle, haystack[, start]): the 1-based position of needle in haystack at or after
-- start, or 0. A start below 1 searches from the beginning; an empty needle is never found (0),
-- where PostgreSQL's strpos would answer 1.
CREATE OR REPLACE FUNCTION {schema}.charindex(needle text, haystack text, start bigint DEFAULT 1) RETURNS integer
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE
    WHEN needle IS NULL OR haystack IS NULL OR start IS NULL THEN NULL
    WHEN needle = '' THEN 0
    WHEN start <= 1 THEN strpos(haystack, needle)
    WHEN strpos(substr(haystack, start::int), needle) = 0 THEN 0
    ELSE strpos(substr(haystack, start::int), needle) + start::int - 1
END
$$;
