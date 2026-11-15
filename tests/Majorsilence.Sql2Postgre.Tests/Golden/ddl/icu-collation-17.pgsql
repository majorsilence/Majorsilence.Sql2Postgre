-- sql2pg: phase Types

-- sql2pg:object Collation public.sql2pg_ci status=Converted
CREATE COLLATION IF NOT EXISTS public.sql2pg_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);

-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=ConvertedWithWarnings
CREATE TABLE public.t (
    id integer NOT NULL,
    name varchar(50) COLLATE public.sql2pg_ci NOT NULL,
    code varchar(10)
);
