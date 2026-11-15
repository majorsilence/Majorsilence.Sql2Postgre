-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=ConvertedWithWarnings
CREATE TABLE public.t (
    v bytea NOT NULL,
    s text,
    h text,
    o timestamptz(6),
    t time(6)
);
