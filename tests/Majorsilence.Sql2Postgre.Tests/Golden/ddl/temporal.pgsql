-- sql2pg: phase Tables

-- sql2pg:object Table public.customer status=Converted
CREATE TABLE public.customer (
    customerid integer NOT NULL,
    name varchar(50) NOT NULL,
    credit numeric(10,2) NOT NULL,
    validfrom timestamp(6) NOT NULL,
    validto timestamp(6) NOT NULL
);

-- sql2pg:object Table public.customerhistory status=Converted
CREATE TABLE public.customerhistory (
    customerid integer NOT NULL,
    name varchar(50) NOT NULL,
    credit numeric(10,2) NOT NULL,
    validfrom timestamp(6) NOT NULL,
    validto timestamp(6) NOT NULL
);

-- sql2pg:object Table public.rate status=Converted
CREATE TABLE public.rate (
    rateid integer NOT NULL,
    amount numeric(10,4) NOT NULL,
    startsat timestamp(0) NOT NULL,
    endsat timestamp(0) NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object View public.customerasof2020 status=Converted
CREATE VIEW public.customerasof2020 AS
SELECT c.customerid, c.name, c.credit
FROM (SELECT customerid, name, credit, validfrom, validto FROM public.customer WHERE validfrom <= '2020-01-01' AND validto > '2020-01-01' UNION ALL SELECT customerid, name, credit, validfrom, validto FROM public.customerhistory WHERE validfrom <= '2020-01-01' AND validto > '2020-01-01') AS c;

-- sql2pg:object Procedure public.customerhistoryfor status=ConvertedWithWarnings
CREATE PROCEDURE public.customerhistoryfor(id integer, "from" timestamp(6), "to" timestamp(6), INOUT result_set_1 refcursor DEFAULT NULL, INOUT result_set_2 refcursor DEFAULT NULL, INOUT result_set_3 refcursor DEFAULT NULL, INOUT result_set_4 refcursor DEFAULT NULL, INOUT result_set_5 refcursor DEFAULT NULL)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    OPEN result_set_1 FOR SELECT name, credit, validfrom
        FROM (SELECT customerid, name, credit, validfrom, validto FROM public.customer WHERE validfrom < customerhistoryfor."to" AND validto > customerhistoryfor."from" UNION ALL SELECT customerid, name, credit, validfrom, validto FROM public.customerhistory WHERE validfrom < customerhistoryfor."to" AND validto > customerhistoryfor."from") AS customer
        WHERE customerid = customerhistoryfor.id
        ORDER BY validfrom NULLS FIRST;
    OPEN result_set_2 FOR SELECT count(*) AS versions
        FROM (SELECT customerid, name, credit, validfrom, validto FROM public.customer WHERE validfrom <= customerhistoryfor."to" AND validto > customerhistoryfor."from" UNION ALL SELECT customerid, name, credit, validfrom, validto FROM public.customerhistory WHERE validfrom <= customerhistoryfor."to" AND validto > customerhistoryfor."from") AS customer;
    OPEN result_set_3 FOR SELECT count(*) AS closed
        FROM (SELECT customerid, name, credit, validfrom, validto FROM public.customer WHERE validfrom >= customerhistoryfor."from" AND validto <= customerhistoryfor."to" UNION ALL SELECT customerid, name, credit, validfrom, validto FROM public.customerhistory WHERE validfrom >= customerhistoryfor."from" AND validto <= customerhistoryfor."to") AS customer;
    OPEN result_set_4 FOR SELECT customer.name
        FROM (SELECT customerid, name, credit, validfrom, validto FROM public.customer UNION ALL SELECT customerid, name, credit, validfrom, validto FROM public.customerhistory) AS customer
        WHERE customer.customerid = customerhistoryfor.id;
    OPEN result_set_5 FOR SELECT rateid
        FROM (SELECT rateid, amount, startsat, endsat FROM public.rate WHERE startsat <= customerhistoryfor."to" AND endsat > customerhistoryfor."to") AS rate;
END;
$$;

-- sql2pg:object Procedure public.loadcustomers status=Manual
CREATE PROCEDURE public.loadcustomers()
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    ALTER TABLE public.customer DISABLE TRIGGER customer_history;
    ALTER TABLE public.customer DISABLE TRIGGER customer_period;
    UPDATE public.customer
    SET credit = credit + 1, validfrom = '2021-01-01';
    ALTER TABLE public.customer ENABLE TRIGGER customer_period;
    ALTER TABLE public.customer ENABLE TRIGGER customer_history;
    /* sql2pg:untranslated S2P5070
    ALTER TABLE dbo.Rate SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.RateHistory));
    */
END;
$$;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.customer.pk_customer status=Converted
ALTER TABLE public.customer ADD CONSTRAINT pk_customer PRIMARY KEY (customerid);

-- sql2pg:object Constraint public.rate.rate_pkey status=Converted
ALTER TABLE public.rate ADD CONSTRAINT rate_pkey PRIMARY KEY (rateid);

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
CREATE FUNCTION public.customer_history()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO public.customerhistory (customerid, name, credit, validfrom, validto)
    VALUES (old.customerid, old.name, old.credit, old.validfrom, (now() AT TIME ZONE 'UTC'));
    RETURN NULL;
END;
$$;
CREATE TRIGGER customer_history AFTER UPDATE OR DELETE ON public.customer
FOR EACH ROW
EXECUTE FUNCTION public.customer_history();

-- sql2pg:object Trigger public.rate.rate_period status=Converted
CREATE FUNCTION public.rate_period()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    new.startsat := (now() AT TIME ZONE 'UTC');
    new.endsat := CAST('9999-12-31 23:59:59' AS timestamp);
    RETURN NEW;
END;
$$;
CREATE TRIGGER rate_period BEFORE INSERT OR UPDATE ON public.rate
FOR EACH ROW
EXECUTE FUNCTION public.rate_period();
