-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id integer NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure dbo.p status=Skipped
-- S2P0201: Procedure dbo.p is not converted yet (routines and triggers come in Stage 3).

-- sql2pg:object View public.v status=Converted
CREATE VIEW public.v AS
SELECT id
FROM public.t;
