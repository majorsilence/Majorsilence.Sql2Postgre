-- sql2pg: phase Schemas

-- sql2pg:object Extension pg_trgm status=Converted
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- sql2pg: phase Tables

-- sql2pg:object Table public.customer status=Converted
CREATE TABLE public.customer (
    customerid integer NOT NULL,
    name varchar(100) NOT NULL,
    email varchar(200),
    code char(8)
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.findbyname status=Converted
CREATE FUNCTION public.findbyname(part varchar(50))
RETURNS TABLE (customerid integer)
LANGUAGE plpgsql
STABLE
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY SELECT CAST(c.customerid AS integer) AS customerid
        FROM public.customer AS c
        WHERE c.name LIKE '_' || findbyname.part OR c.name LIKE findbyname.part || '%';
END;
$$;

-- sql2pg:object View public.smiths status=Converted
CREATE VIEW public.smiths AS
SELECT customerid, name
FROM public.customer
WHERE name LIKE '%smith%' OR CAST(code AS text) LIKE '%7';

-- sql2pg: phase Indexes

-- sql2pg:object Index dbo.Customer.CustomerId status=Skipped
-- S2P2022: The trigram index on dbo.Customer.CustomerId is not created: it is not a varchar, nvarchar or text column of a converted table.

-- sql2pg:object Index nonsense status=Skipped
-- S2P2022: The trigram index 'nonsense' is not schema.table.column; it is not created.

-- sql2pg:object Index public.trgm_customer_email status=Converted
CREATE INDEX trgm_customer_email ON public.customer USING gin (email gin_trgm_ops);

-- sql2pg:object Index public.trgm_customer_name status=Converted
CREATE INDEX trgm_customer_name ON public.customer USING gin (name gin_trgm_ops);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.customer.customer_pkey status=Converted
ALTER TABLE public.customer ADD CONSTRAINT customer_pkey PRIMARY KEY (customerid);
