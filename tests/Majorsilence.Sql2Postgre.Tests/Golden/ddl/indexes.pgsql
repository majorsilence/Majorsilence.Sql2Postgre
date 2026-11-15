-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id integer NOT NULL,
    email varchar(200),
    name varchar(100) NOT NULL,
    active boolean NOT NULL
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ix_t_email status=Converted
CREATE UNIQUE INDEX ix_t_email ON public.t (email DESC) INCLUDE (name) WHERE active = true AND email IS NOT NULL;

-- sql2pg:object Index public.ix_t_name status=Converted
CREATE INDEX ix_t_name ON public.t (name);

-- sql2pg:object Index public.ncci_t status=Skipped
-- S2P2003: columnstore index ncci_t on dbo.t has no PostgreSQL equivalent and is not created.
