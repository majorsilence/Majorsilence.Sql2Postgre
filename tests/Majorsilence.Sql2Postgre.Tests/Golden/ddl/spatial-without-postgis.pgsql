-- sql2pg: phase Tables

-- sql2pg:object Table public.place status=ConvertedWithWarnings
CREATE TABLE public.place (
    id integer NOT NULL,
    loc bytea
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.six_place_loc status=Skipped
-- S2P2003: spatial index six_place_loc on dbo.place has no PostgreSQL equivalent and is not created.

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.place.place_pkey status=Converted
ALTER TABLE public.place ADD CONSTRAINT place_pkey PRIMARY KEY (id);
