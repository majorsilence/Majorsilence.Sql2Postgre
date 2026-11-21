-- TRY_CONVERT(datetime2 | date, s, 126 | 127): s read as SQL Server reads it under those styles,
-- as established on SQL Server 2022:
--   yyyy-mm-dd (or yyyy/mm/dd), then optionally an upper-case T or a space and hh:mi:ss with a
--   fraction of any length, then optionally Z or an offset up to 14:00, which SQL Server ignores
--   (the time is kept as written); or yyyymmdd. A blank string is 1900-01-01.
-- Anything else (a date or time that does not exist, 24:00:00, a month name), or NULL, is NULL.
-- The regular expressions run on the default collation, which a column's nondeterministic
-- (case-insensitive) collation does not allow them on.
-- IMMUTABLE: it reads no setting (make_timestamp, not a cast from text), so a generated column
-- may call it. The fraction is kept to the microsecond; the caller casts to the target scale.
CREATE OR REPLACE FUNCTION {schema}.try_convert_iso8601(s text) RETURNS timestamp
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    m text[];
BEGIN
    IF btrim(s, ' ') = '' THEN
        RETURN '1900-01-01 00:00:00';
    END IF;
    m := regexp_match(s COLLATE "default", '^([0-9]{4})([-/])([0-9]{2})\2([0-9]{2})(?:[T ]([0-9]{2}):([0-9]{2}):([0-9]{2}(?:\.[0-9]+)?))?(?:Z|[+-]([0-9]{2}):([0-9]{2}))?$');
    IF m IS NULL THEN
        m := regexp_match(s COLLATE "default", '^([0-9]{4})()([0-9]{2})([0-9]{2})$');
    END IF;
    IF m IS NULL OR coalesce(m[5]::int, 0) > 23 OR coalesce(m[6]::int, 0) > 59 OR coalesce(m[7]::numeric, 0) >= 60
            OR coalesce(m[8]::int * 60 + m[9]::int, 0) > 14 * 60 OR coalesce(m[9]::int, 0) > 59 THEN
        RETURN NULL;
    END IF;
    RETURN make_timestamp(m[1]::int, m[3]::int, m[4]::int, coalesce(m[5], '0')::int, coalesce(m[6], '0')::int,
                          coalesce(m[7], '0')::double precision);
EXCEPTION WHEN others THEN
    RETURN NULL;
END
$$;
