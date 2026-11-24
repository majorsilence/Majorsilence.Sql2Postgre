-- sql2pg: phase Tables

-- sql2pg:object Function public.ufnleadingzeros status=Converted
CREATE FUNCTION public.ufnleadingzeros(value integer)
RETURNS varchar(8)
LANGUAGE plpgsql
IMMUTABLE
AS $$
#variable_conflict use_column
DECLARE
    v_returnvalue varchar(8);
BEGIN
    v_returnvalue := CAST(ufnleadingzeros.value AS varchar(8));
    v_returnvalue := CAST(repeat('0', 8 - octet_length(v_returnvalue)) || v_returnvalue AS varchar(8));
    RETURN v_returnvalue;
END;
$$;

-- sql2pg:object Function public.ufnprefixed status=Converted
CREATE FUNCTION public.ufnprefixed(prefix varchar(2), value integer)
RETURNS varchar(10)
LANGUAGE sql
IMMUTABLE
RETURN CAST(ufnprefixed.prefix || public.ufnleadingzeros(ufnprefixed.value) AS varchar(10));

-- sql2pg:object Table public.customer status=Manual
CREATE TABLE public.customer (
    customerid integer NOT NULL,
    accountnumber text GENERATED ALWAYS AS (COALESCE('AW' || public.ufnleadingzeros(customerid), '')) STORED,
    code varchar(10) DEFAULT public.ufnprefixed('C', 0) NOT NULL
    /* sql2pg:untranslated S2P3001
    Position AS (dbo.ufnCustomerCount())
    */
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Function public.ufncustomercount status=Converted
CREATE FUNCTION public.ufncustomercount()
RETURNS integer
LANGUAGE sql
STABLE
RETURN (SELECT count(*) FROM public.customer);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.customer.customer_pkey status=Converted
ALTER TABLE public.customer ADD CONSTRAINT customer_pkey PRIMARY KEY (customerid);
