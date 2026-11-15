-- sql2pg: phase Tables

-- sql2pg:object Table public.p status=Converted
CREATE TABLE public.p (
    id integer NOT NULL
);

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id integer NOT NULL,
    p integer,
    qty integer NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.p.p_pkey status=Converted
ALTER TABLE public.p ADD CONSTRAINT p_pkey PRIMARY KEY (id);

-- sql2pg:object Constraint public.t.ck_qty status=ConvertedWithWarnings
ALTER TABLE public.t ADD CONSTRAINT ck_qty CHECK (qty > 0) NOT VALID;

-- sql2pg: phase ForeignKeys

-- sql2pg:object Constraint public.t.fk_t_p status=Converted
ALTER TABLE public.t ADD CONSTRAINT fk_t_p FOREIGN KEY (p) REFERENCES public.p (id) NOT VALID;
