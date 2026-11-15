-- sql2pg: phase Schemas

-- sql2pg:object Schema sales status=Converted
CREATE SCHEMA IF NOT EXISTS sales;

-- sql2pg: phase Tables

-- sql2pg:object Table public.lookup status=Converted
CREATE TABLE public.lookup (
    id integer NOT NULL
);

-- sql2pg:object Table sales."order" status=Converted
CREATE TABLE sales."order" (
    orderid integer NOT NULL,
    "user" varchar(50)
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint sales."order".order_pkey status=Converted
ALTER TABLE sales."order" ADD CONSTRAINT order_pkey PRIMARY KEY (orderid);
