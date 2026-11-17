-- sql2pg: phase Tables

-- sql2pg:object Table public.customer status=Converted
CREATE TABLE public.customer (
    customerid integer NOT NULL,
    name varchar(50) NOT NULL
);

-- sql2pg:object Table public.sale status=Converted
CREATE TABLE public.sale (
    saleid integer NOT NULL,
    customerid integer NOT NULL,
    amount numeric(10,2) NOT NULL,
    region varchar(10) DEFAULT 'north'
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.loadsales status=ConvertedWithWarnings
CREATE PROCEDURE public.loadsales()
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    ALTER TABLE public.sale DROP CONSTRAINT IF EXISTS fk_sale_customer;
    ALTER TABLE public.sale DROP CONSTRAINT pk_sale;
    DROP INDEX IF EXISTS public.ix_sale_customer;
    DROP INDEX public.ix_sale_region;
    DROP INDEX IF EXISTS public.ix_sale_region;
    INSERT INTO public.sale (saleid, customerid, amount)
    VALUES (100, 1, 5.00);
    ALTER TABLE public.sale ADD CONSTRAINT pk_sale PRIMARY KEY (saleid);
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = to_regclass('public.sale') AND conname = 'fk_sale_customer') THEN
        ALTER TABLE public.sale ADD CONSTRAINT fk_sale_customer FOREIGN KEY (customerid) REFERENCES public.customer (customerid) NOT VALID;
    END IF;
    ALTER TABLE public.sale VALIDATE CONSTRAINT fk_sale_customer;
    DROP INDEX IF EXISTS public.ix_sale_customer;
    CREATE INDEX ix_sale_customer ON public.sale (customerid DESC) INCLUDE (amount);
    IF to_regclass('public.ix_sale_region') IS NULL THEN
        CREATE INDEX ix_sale_region ON public.sale (region) WHERE region IS NOT NULL;
    ELSE
        REINDEX INDEX public.ix_sale_region;
    END IF;
    REINDEX INDEX public.ix_sale_customer;
    REINDEX TABLE public.sale;
END;
$$;

-- sql2pg:object Procedure public.reshapesales status=Manual
CREATE PROCEDURE public.reshapesales()
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    /* sql2pg:untranslated S2P2003
    CREATE CLUSTERED COLUMNSTORE INDEX CCX_Sale ON dbo.Sale;
    */
    ALTER TABLE public.sale DROP CONSTRAINT IF EXISTS fk_sale_customer;
    ALTER TABLE public.sale DROP CONSTRAINT IF EXISTS ck_sale_amount;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = to_regclass('public.sale') AND conname = 'fk_sale_customer') THEN
        ALTER TABLE public.sale ADD CONSTRAINT fk_sale_customer FOREIGN KEY (customerid) REFERENCES public.customer (customerid) NOT VALID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = to_regclass('public.sale') AND conname = 'ck_sale_amount') THEN
        ALTER TABLE public.sale ADD CONSTRAINT ck_sale_amount CHECK (amount >= 0) NOT VALID;
    END IF;
    ALTER TABLE public.sale ADD COLUMN note varchar(100);
    ALTER TABLE public.sale ADD COLUMN rank integer DEFAULT 0 NOT NULL;
    ALTER TABLE public.sale ADD CONSTRAINT ck_sale_rank CHECK (rank >= 0) NOT VALID;
    ALTER TABLE public.sale DROP CONSTRAINT IF EXISTS ck_sale_rank;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = to_regclass('public.sale') AND conname = 'ck_sale_rank') THEN
        ALTER TABLE public.sale ADD CONSTRAINT ck_sale_rank CHECK (rank >= 0) NOT VALID;
    END IF;
    ALTER TABLE public.sale
        ALTER COLUMN note TYPE varchar(200) USING note::varchar(200),
        ALTER COLUMN note SET NOT NULL;
    ALTER TABLE public.sale ALTER COLUMN region DROP DEFAULT;
    ALTER TABLE public.sale ALTER COLUMN region SET DEFAULT 'south';
    ALTER TABLE public.sale DROP CONSTRAINT ck_sale_rank;
    ALTER TABLE public.sale DROP COLUMN note;
    ALTER TABLE public.sale DROP COLUMN rank;
    /* sql2pg:untranslated S2P5070
    ALTER TABLE dbo.Sale NOCHECK CONSTRAINT FK_Unknown;
    */
    DROP ROUTINE IF EXISTS public.loadsales;
    DROP ROUTINE public.helper;
    /* sql2pg:untranslated S2P5070
    ALTER TABLE dbo.Sale SET (LOCK_ESCALATION = AUTO);
    */
    /* sql2pg:untranslated S2P5070
    ALTER TABLE dbo.Sale SET (SYSTEM_VERSIONING = OFF);
    */
    /* sql2pg:untranslated S2P5070
    ALTER TABLE dbo.Sale DROP PERIOD FOR SYSTEM_TIME;
    */
END;
$$;

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ix_sale_customer status=Converted
CREATE INDEX ix_sale_customer ON public.sale (customerid) INCLUDE (amount);

-- sql2pg:object Index public.ix_sale_region status=Converted
CREATE INDEX ix_sale_region ON public.sale (region) WHERE region IS NOT NULL;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.customer.pk_customer status=Converted
ALTER TABLE public.customer ADD CONSTRAINT pk_customer PRIMARY KEY (customerid);

-- sql2pg:object Constraint public.sale.ck_sale_amount status=Converted
ALTER TABLE public.sale ADD CONSTRAINT ck_sale_amount CHECK (amount >= 0);

-- sql2pg:object Constraint public.sale.pk_sale status=Converted
ALTER TABLE public.sale ADD CONSTRAINT pk_sale PRIMARY KEY (saleid);

-- sql2pg: phase ForeignKeys

-- sql2pg:object Constraint public.sale.fk_sale_customer status=Converted
ALTER TABLE public.sale ADD CONSTRAINT fk_sale_customer FOREIGN KEY (customerid) REFERENCES public.customer (customerid);
