-- DATEDIFF(part, a, b): the number of part boundaries crossed between a and b, as SQL Server
-- counts them (not the elapsed time): DATEDIFF(year, '2024-12-31', '2025-01-01') is 1. Weeks
-- start on Sunday whatever SET DATEFIRST says, as in SQL Server.
CREATE OR REPLACE FUNCTION {schema}.datediff(part text, a timestamp, b timestamp) RETURNS bigint
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE part
    WHEN 'year' THEN extract(year FROM b)::bigint - extract(year FROM a)::bigint
    WHEN 'quarter' THEN (extract(year FROM b)::bigint * 4 + extract(quarter FROM b)::bigint)
                      - (extract(year FROM a)::bigint * 4 + extract(quarter FROM a)::bigint)
    WHEN 'month' THEN (extract(year FROM b)::bigint * 12 + extract(month FROM b)::bigint)
                    - (extract(year FROM a)::bigint * 12 + extract(month FROM a)::bigint)
    WHEN 'day' THEN (b::date - a::date)::bigint
    WHEN 'dayofyear' THEN (b::date - a::date)::bigint
    WHEN 'weekday' THEN (b::date - a::date)::bigint
    WHEN 'week' THEN (((b::date - extract(dow FROM b)::int) - (a::date - extract(dow FROM a)::int)) / 7)::bigint
    WHEN 'hour' THEN (extract(epoch FROM date_trunc('hour', b) - date_trunc('hour', a)) / 3600)::bigint
    WHEN 'minute' THEN (extract(epoch FROM date_trunc('minute', b) - date_trunc('minute', a)) / 60)::bigint
    WHEN 'second' THEN extract(epoch FROM date_trunc('second', b) - date_trunc('second', a))::bigint
    WHEN 'millisecond' THEN (extract(epoch FROM date_trunc('milliseconds', b) - date_trunc('milliseconds', a)) * 1000)::bigint
    WHEN 'microsecond' THEN (extract(epoch FROM b - a) * 1000000)::bigint
END
$$;
