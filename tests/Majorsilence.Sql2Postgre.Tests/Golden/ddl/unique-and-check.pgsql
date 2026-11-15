-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id integer NOT NULL,
    code varchar(10) NOT NULL,
    qty integer NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.t.ck_t_qty status=Converted
ALTER TABLE public.t ADD CONSTRAINT ck_t_qty CHECK (qty > 0);

-- sql2pg:object Constraint public.t.t_check status=Converted
ALTER TABLE public.t ADD CONSTRAINT t_check CHECK (code <> '');

-- sql2pg:object Constraint public.t.t_id_qty_key status=Converted
ALTER TABLE public.t ADD CONSTRAINT t_id_qty_key UNIQUE (id, qty);

-- sql2pg:object Constraint public.t.uq_t_code status=Converted
ALTER TABLE public.t ADD CONSTRAINT uq_t_code UNIQUE (code);
