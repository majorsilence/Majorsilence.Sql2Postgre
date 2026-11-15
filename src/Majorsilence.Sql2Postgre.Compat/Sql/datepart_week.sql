-- DATEPART(week, d) with SET DATEFIRST 7 (SQL Server's default): week 1 is the week holding
-- January 1, and weeks start on Sunday.
CREATE OR REPLACE FUNCTION {schema}.datepart_week(d timestamp) RETURNS integer
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT ((extract(doy FROM d)::int - 1 + extract(dow FROM date_trunc('year', d))::int) / 7) + 1
$$;
