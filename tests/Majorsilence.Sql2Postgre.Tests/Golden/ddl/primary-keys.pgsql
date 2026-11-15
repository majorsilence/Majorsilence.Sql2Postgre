-- sql2pg: phase Tables

-- sql2pg:object Table public.a status=Converted
CREATE TABLE public.a (
    id integer NOT NULL
);

-- sql2pg:object Table public.b status=Converted
CREATE TABLE public.b (
    id integer NOT NULL,
    x integer NOT NULL
);

-- sql2pg:object Table public.c status=Converted
CREATE TABLE public.c (
    id integer NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.a.pk_a status=Converted
ALTER TABLE public.a ADD CONSTRAINT pk_a PRIMARY KEY (id);

-- sql2pg:object Constraint public.b.pk_b status=Converted
ALTER TABLE public.b ADD CONSTRAINT pk_b PRIMARY KEY (id, x);

-- sql2pg:object Constraint public.c.c_pkey status=Converted
ALTER TABLE public.c ADD CONSTRAINT c_pkey PRIMARY KEY (id);
