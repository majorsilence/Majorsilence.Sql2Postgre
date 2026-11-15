-- sql2pg: phase Tables

-- sql2pg:object Table public.item status=Converted
CREATE TABLE public.item (
    id integer NOT NULL,
    name varchar(50) NOT NULL,
    price numeric(19,4) NOT NULL,
    active boolean NOT NULL
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Function dbo.fmt status=Skipped
-- S2P0201: Function dbo.fmt is not converted yet (routines and triggers come in Stage 3).

-- sql2pg:object View public.v_active status=Converted
CREATE VIEW public.v_active AS
SELECT id, name
FROM public.item
WHERE active = true
WITH CHECK OPTION;

-- sql2pg:object View public.v_indexed status=ConvertedWithWarnings
CREATE MATERIALIZED VIEW public.v_indexed AS
SELECT id, price
FROM public.item;

-- sql2pg:object View public.v_named status=Converted
CREATE VIEW public.v_named (item_id, label) AS
SELECT id, upper(name)
FROM public.item;

-- sql2pg:object View public.a_over_view status=Converted
CREATE VIEW public.a_over_view AS
SELECT label
FROM public.v_named
WHERE item_id > 1;

-- sql2pg:object View public.v_sorted status=Converted
CREATE VIEW public.v_sorted AS
SELECT id, name
FROM public.item;

-- sql2pg:object View public.v_top status=Converted
CREATE VIEW public.v_top AS
SELECT id
FROM public.item
ORDER BY price DESC NULLS LAST
LIMIT 3;

-- sql2pg:object View public.v_untranslatable status=Manual
/* sql2pg:untranslated S2P4010
CREATE VIEW dbo.v_untranslatable AS SELECT id FROM dbo.item FOR XML RAW;
*/

-- sql2pg:object View public.v_uses_function status=Manual
CREATE VIEW public.v_uses_function AS
SELECT id, public.fmt(price) AS shown
FROM public.item;

-- sql2pg:object View public.v_over_blocked status=Manual
CREATE VIEW public.v_over_blocked AS
SELECT shown
FROM public.v_uses_function;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.item.item_pkey status=Converted
ALTER TABLE public.item ADD CONSTRAINT item_pkey PRIMARY KEY (id);
