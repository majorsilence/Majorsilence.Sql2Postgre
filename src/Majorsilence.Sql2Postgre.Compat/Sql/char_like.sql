-- LIKE on a char(n) value with a non-Unicode pattern, as SQL Server matches it: the value's
-- trailing spaces may be left off, any number of them ('ab    ' matches 'ab', 'ab_' and '______',
-- not '_______'). value is the padded value as text; the pattern's own spaces all count.
CREATE OR REPLACE FUNCTION {schema}.char_like(value text, pattern text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE
RETURN (SELECT bool_or(left(value, length(value) - k) LIKE pattern)
        FROM generate_series(0, length(value) - length(rtrim(value, ' '))) AS k);
