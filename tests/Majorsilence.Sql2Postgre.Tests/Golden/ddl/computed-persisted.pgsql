-- sql2pg: phase Tables

-- sql2pg:object Table public.line status=Converted
CREATE TABLE public.line (
    qty integer NOT NULL,
    price numeric(10,2) NOT NULL,
    total numeric GENERATED ALWAYS AS (qty * price) STORED,
    tax numeric GENERATED ALWAYS AS (qty * price * 0.1) STORED
);
