-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.datediff status=Converted
-- DATEDIFF(part, a, b): the number of part boundaries crossed between a and b, as SQL Server
-- counts them (not the elapsed time): DATEDIFF(year, '2024-12-31', '2025-01-01') is 1. Weeks
-- start on Sunday whatever SET DATEFIRST says, as in SQL Server.
CREATE OR REPLACE FUNCTION mssql_compat.datediff(part text, a timestamp, b timestamp) RETURNS bigint
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

-- sql2pg: phase Tables

-- sql2pg:object Table public.item status=Converted
CREATE TABLE public.item (
    id integer NOT NULL,
    name varchar(50) NOT NULL,
    price numeric(10,2) NOT NULL,
    added timestamp(6) NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Function public.age_days status=Converted
CREATE FUNCTION public.age_days(since timestamp(6))
RETURNS integer
LANGUAGE sql
STABLE
RETURN mssql_compat.datediff('day', age_days.since, localtimestamp);

-- sql2pg:object Function public.cheap_items status=Converted
CREATE FUNCTION public.cheap_items(max numeric(10,2), prefix varchar(10) DEFAULT '', "limit" integer DEFAULT NULL)
RETURNS TABLE (id integer, name varchar(50), price numeric(10,2))
LANGUAGE sql
STABLE
AS $$
SELECT id, name, price
FROM public.item
WHERE price <= cheap_items.max AND name LIKE cheap_items.prefix || '%';
$$;

-- sql2pg:object Function public.item_name status=Converted
CREATE FUNCTION public.item_name(id integer)
RETURNS varchar(50)
LANGUAGE sql
STABLE
RETURN (SELECT name FROM public.item WHERE id = item_name.id);

-- sql2pg:object Function public.item_row status=Converted
CREATE FUNCTION public.item_row(p_id integer)
RETURNS TABLE (id integer, name varchar(50))
LANGUAGE plpgsql
VOLATILE
AS $$
#variable_conflict use_column
BEGIN
    DROP TABLE IF EXISTS _ret_item_row;
    CREATE TEMP TABLE _ret_item_row (
        id integer,
        name varchar(50)
    ) ON COMMIT DROP;
    INSERT INTO _ret_item_row (id, name)
    SELECT id, name
    FROM public.item
    WHERE id = item_row.p_id;
    RETURN QUERY SELECT *
        FROM _ret_item_row;
    RETURN;
END;
$$;

-- sql2pg:object Function public.leading_zeros status=Converted
CREATE FUNCTION public.leading_zeros(value integer)
RETURNS varchar(8)
LANGUAGE plpgsql
IMMUTABLE
AS $$
#variable_conflict use_column
DECLARE
    v_r varchar(8);
BEGIN
    v_r := CAST(leading_zeros.value AS varchar(8));
    v_r := CAST(repeat('0', 8 - length(rtrim(v_r))) || v_r AS varchar(8));
    RETURN v_r;
END;
$$;

-- sql2pg:object Function public.price_bands status=Converted
CREATE FUNCTION public.price_bands(step numeric(10,2))
RETURNS TABLE (band integer, items integer, total numeric(12,2))
LANGUAGE plpgsql
VOLATILE
AS $$
#variable_conflict use_column
BEGIN
    DROP TABLE IF EXISTS _ret_price_bands;
    CREATE TEMP TABLE _ret_price_bands (
        band integer NOT NULL,
        items integer NOT NULL,
        total numeric(12,2)
    ) ON COMMIT DROP;
    ALTER TABLE _ret_price_bands ADD CONSTRAINT _ret_price_bands_pkey PRIMARY KEY (band);
    INSERT INTO _ret_price_bands (band, items, total)
    SELECT CAST(trunc(price / price_bands.step) AS integer), count(*), sum(price)
    FROM public.item
    GROUP BY CAST(trunc(price / price_bands.step) AS integer);
    UPDATE _ret_price_bands
    SET total = 0
    WHERE total IS NULL;
    IF NOT EXISTS (SELECT 1 FROM _ret_price_bands) THEN
        INSERT INTO _ret_price_bands (band, items)
        VALUES (-1, 0);
    END IF;
    RETURN QUERY SELECT *
        FROM _ret_price_bands;
    RETURN;
END;
$$;

-- sql2pg:object View public.item_bands status=Converted
CREATE VIEW public.item_bands AS
SELECT i.id, b.band
FROM public.item AS i CROSS JOIN LATERAL public.price_bands(i.price) AS b;

-- sql2pg:object Function public.with_tax status=Converted
CREATE FUNCTION public.with_tax(amount numeric(10,2), rate numeric(5,4) DEFAULT 0.2)
RETURNS integer
LANGUAGE sql
IMMUTABLE
RETURN CAST(trunc(with_tax.amount * (1 + with_tax.rate)) AS integer);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.item.item_pkey status=Converted
ALTER TABLE public.item ADD CONSTRAINT item_pkey PRIMARY KEY (id);
