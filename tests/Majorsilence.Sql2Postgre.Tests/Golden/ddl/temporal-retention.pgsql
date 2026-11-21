-- sql2pg: phase Tables

-- sql2pg:object Table public.price status=Converted
CREATE TABLE public.price (
    id integer NOT NULL,
    amount numeric(10,2) NOT NULL,
    validfrom timestamp(6) NOT NULL,
    validto timestamp(6) NOT NULL
);

-- sql2pg:object Table public.pricehistory status=Converted
CREATE TABLE public.pricehistory (
    id integer NOT NULL,
    amount numeric(10,2) NOT NULL,
    validfrom timestamp(6) NOT NULL,
    validto timestamp(6) NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.price.price_pkey status=Converted
ALTER TABLE public.price ADD CONSTRAINT price_pkey PRIMARY KEY (id);

-- sql2pg: phase Triggers

-- sql2pg:object Trigger public.price.price_period status=ConvertedWithWarnings
CREATE FUNCTION public.price_period()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    new.validfrom := (now() AT TIME ZONE 'UTC');
    new.validto := CAST('9999-12-31 23:59:59.999999' AS timestamp);
    RETURN NEW;
END;
$$;
CREATE TRIGGER price_period BEFORE INSERT OR UPDATE ON public.price
FOR EACH ROW
EXECUTE FUNCTION public.price_period();
CREATE FUNCTION public.price_history()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO public.pricehistory (id, amount, validfrom, validto)
    VALUES (old.id, old.amount, old.validfrom, (now() AT TIME ZONE 'UTC'));
    RETURN NULL;
END;
$$;
CREATE TRIGGER price_history AFTER UPDATE OR DELETE ON public.price
FOR EACH ROW
EXECUTE FUNCTION public.price_history();
