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

-- sql2pg:object Function public.blocked status=Manual
CREATE FUNCTION public.blocked()
RETURNS integer
LANGUAGE plpgsql
IMMUTABLE
AS $$
#variable_conflict use_column
BEGIN
    /* sql2pg:untranslated S2P5005
    retry:
    */
    /* sql2pg:untranslated S2P5005
    GOTO retry;
    */
    RETURN 1;
END;
$$;

-- sql2pg:object Function public.fmt status=Converted
CREATE FUNCTION public.fmt(p numeric(19,4))
RETURNS varchar(20)
LANGUAGE sql
IMMUTABLE
RETURN CAST(fmt.p AS varchar(20));

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

-- sql2pg:object View public.v_uses_blocked status=Manual
CREATE VIEW public.v_uses_blocked AS
SELECT id, public.blocked() AS b
FROM public.item;

-- sql2pg:object View public.v_over_blocked status=Manual
CREATE VIEW public.v_over_blocked AS
SELECT b
FROM public.v_uses_blocked;

-- sql2pg:object View public.v_uses_function status=Converted
CREATE VIEW public.v_uses_function AS
SELECT id, public.fmt(price) AS shown
FROM public.item;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.item.item_pkey status=Converted
ALTER TABLE public.item ADD CONSTRAINT item_pkey PRIMARY KEY (id);
