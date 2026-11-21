-- ISNUMERIC(s): 1 when SQL Server could convert s to some numeric type (int, decimal, float or
-- money), else 0. That includes a lone sign, a lone point, a lone comma, currency symbols,
-- thousands separators and d/e exponents, as SQL Server accepts them; NULL gives 0.
-- The regular expressions run on the default collation: s may come from a column with a
-- nondeterministic (case-insensitive) collation, on which no PostgreSQL version allows them.
CREATE OR REPLACE FUNCTION {schema}.isnumeric(s text) RETURNS integer
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE
    WHEN s IS NULL THEN 0
    WHEN btrim(s, E' \t\r\n') COLLATE "default" ~ '^[+-]?[$£€¥]?[+-]?([0-9,]+[.]?[0-9]*|[.][0-9]+|[.]?)([eEdD][+-]?[0-9]+)?$'
         AND btrim(s, E' \t\r\n') COLLATE "default" ~ '[0-9+\-.,$£€¥]' THEN 1
    ELSE 0
END
$$;
