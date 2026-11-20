-- sql2pg: phase Tables

-- sql2pg:object Table public.customer status=ConvertedWithWarnings
CREATE TABLE public.customer (
    id integer NOT NULL,
    email varchar(100),
    phone varchar(20),
    taxid char(11),
    validfrom timestamp(6) NOT NULL,
    validto timestamp(6) NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.customer.customer_pkey status=Converted
ALTER TABLE public.customer ADD CONSTRAINT customer_pkey PRIMARY KEY (id);

-- sql2pg: phase Triggers

-- sql2pg:object Trigger public.customer.customer_period status=Converted
CREATE FUNCTION public.customer_period()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    new.validfrom := (now() AT TIME ZONE 'UTC');
    new.validto := CAST('9999-12-31 23:59:59.999999' AS timestamp);
    RETURN NEW;
END;
$$;
CREATE TRIGGER customer_period BEFORE INSERT OR UPDATE ON public.customer
FOR EACH ROW
EXECUTE FUNCTION public.customer_period();
