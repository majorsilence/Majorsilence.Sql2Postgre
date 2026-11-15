-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id integer NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure dbo.p status=Skipped
-- S2P0201: Procedure dbo.p is not converted yet (views in Stage 2, routines and triggers in Stage 3).

-- sql2pg:object View dbo.v status=Skipped
-- S2P0201: View dbo.v is not converted yet (views in Stage 2, routines and triggers in Stage 3).
