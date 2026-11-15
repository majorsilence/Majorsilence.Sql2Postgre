-- sql2pg: phase Tables

-- sql2pg:object Table public.line status=Converted
CREATE TABLE public.line (
    qty integer NOT NULL,
    price numeric(10,2) NOT NULL,
    total numeric(21,2) GENERATED ALWAYS AS (qty * price) STORED
);
