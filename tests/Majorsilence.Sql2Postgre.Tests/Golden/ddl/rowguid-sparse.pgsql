-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    note varchar(100)
);
