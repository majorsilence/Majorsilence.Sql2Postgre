-- sql2pg: phase Tables

-- sql2pg:object Table public.orderdoc status=ConvertedWithWarnings
CREATE TABLE public.orderdoc (
    id integer NOT NULL,
    body xml,
    notes xml,
    free xml
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.orderdoc.orderdoc_pkey status=Converted
ALTER TABLE public.orderdoc ADD CONSTRAINT orderdoc_pkey PRIMARY KEY (id);
