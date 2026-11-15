-- sql2pg: phase Tables

-- sql2pg:object Table public.orders status=Converted
CREATE TABLE public.orders (
    id integer NOT NULL,
    n integer NOT NULL,
    number text GENERATED ALWAYS AS ('SO' || CAST(n AS varchar(10))) STORED,
    label integer GENERATED ALWAYS AS (n * 2) VIRTUAL
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ak_orders_number status=Converted
CREATE UNIQUE INDEX ak_orders_number ON public.orders (number);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.orders.orders_pkey status=Converted
ALTER TABLE public.orders ADD CONSTRAINT orders_pkey PRIMARY KEY (id);
