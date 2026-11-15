-- sql2pg: phase Types

-- sql2pg:object UserType public.code status=Converted
CREATE DOMAIN public.code AS char(3) NOT NULL;

-- sql2pg:object UserType public.linelist status=Converted
CREATE TYPE public.linelist AS (
    product_id integer,
    qty integer
);

-- sql2pg:object UserType public.phone status=Converted
CREATE DOMAIN public.phone AS varchar(20);

-- sql2pg: phase Tables

-- sql2pg:object Table public.contact status=Converted
CREATE TABLE public.contact (
    id integer NOT NULL,
    phone public.phone,
    code public.code
);
