-- sql2pg: phase Tables

-- sql2pg:object Table public.ledger status=Converted
CREATE TABLE public.ledger (
    id integer NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.addledgercolumns status=ConvertedWithWarnings
CREATE PROCEDURE public.addledgercolumns()
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    ALTER TABLE public.ledger ADD COLUMN flag integer;
    ALTER TABLE public.ledger ALTER COLUMN flag SET DEFAULT 0;
    ALTER TABLE public.ledger ADD COLUMN kept integer DEFAULT 1;
    ALTER TABLE public.ledger ADD COLUMN required integer DEFAULT 2 NOT NULL;
END;
$$;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.ledger.ledger_pkey status=Converted
ALTER TABLE public.ledger ADD CONSTRAINT ledger_pkey PRIMARY KEY (id);
