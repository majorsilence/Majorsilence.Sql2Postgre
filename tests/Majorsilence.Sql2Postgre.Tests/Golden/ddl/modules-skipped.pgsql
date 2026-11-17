-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Converted
CREATE TABLE public.t (
    id integer NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.p status=Converted
CREATE FUNCTION public.p()
RETURNS TABLE (column1 integer)
LANGUAGE plpgsql
IMMUTABLE
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY SELECT CAST(1 AS integer) AS column1;
END;
$$;

-- sql2pg:object View public.v status=Converted
CREATE VIEW public.v AS
SELECT id
FROM public.t;

-- sql2pg: phase Triggers

-- sql2pg:object Trigger public.t_insert status=Converted
CREATE FUNCTION public.t_insert()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    RETURN NULL;
END;
$$;
CREATE TRIGGER t_insert AFTER INSERT ON public.t
REFERENCING NEW TABLE AS inserted
FOR EACH STATEMENT
EXECUTE FUNCTION public.t_insert();
