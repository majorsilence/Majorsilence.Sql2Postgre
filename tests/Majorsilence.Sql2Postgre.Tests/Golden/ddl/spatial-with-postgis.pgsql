-- sql2pg: phase Schemas

-- sql2pg:object Extension postgis status=Converted
CREATE EXTENSION IF NOT EXISTS postgis;

-- sql2pg: phase Tables

-- sql2pg:object Table public.place status=Converted
CREATE TABLE public.place (
    id integer NOT NULL,
    loc geography
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.six_place_loc status=Converted
CREATE INDEX six_place_loc ON public.place USING gist (loc);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.place.place_pkey status=Converted
ALTER TABLE public.place ADD CONSTRAINT place_pkey PRIMARY KEY (id);
