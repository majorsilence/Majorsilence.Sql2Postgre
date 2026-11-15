-- sql2pg: phase Tables

-- sql2pg:object Table public.order_line status=Converted
CREATE TABLE public.order_line (
    order_line_id integer NOT NULL,
    product_name varchar(50) NOT NULL
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ix_order_line_product_name status=Converted
CREATE INDEX ix_order_line_product_name ON public.order_line (product_name);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.order_line.pk_order_line status=Converted
ALTER TABLE public.order_line ADD CONSTRAINT pk_order_line PRIMARY KEY (order_line_id);
