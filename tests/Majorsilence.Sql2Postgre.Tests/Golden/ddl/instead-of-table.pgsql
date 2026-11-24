-- sql2pg: phase Tables

-- sql2pg:object Table public.vendor status=Converted
CREATE TABLE public.vendor (
    vendorid integer NOT NULL,
    name varchar(50) NOT NULL,
    activeflag boolean DEFAULT true NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.vendor.vendor_pkey status=Converted
ALTER TABLE public.vendor ADD CONSTRAINT vendor_pkey PRIMARY KEY (vendorid);

-- sql2pg: phase Triggers

-- sql2pg:object Trigger public.dvendor status=ConvertedWithWarnings
CREATE FUNCTION public.dvendor()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    sql2pg_rolled_back boolean;
    sql2pg_rowcount integer;
    v_count integer;
BEGIN
    IF current_setting('sql2pg.instead_of_dvendor', true) = 'on' THEN
        RETURN OLD;
    END IF;
    PERFORM set_config('sql2pg.instead_of_dvendor', 'on', true);
    sql2pg_rowcount := 1;
    v_count := sql2pg_rowcount;
    sql2pg_rowcount := 1;
    IF v_count = 0 THEN
        IF sql2pg_rolled_back THEN
            RAISE EXCEPTION 'transaction rolled back by trigger %', TG_NAME;
        END IF;
        PERFORM set_config('sql2pg.instead_of_dvendor', '', true);
        RETURN NULL;
    END IF;
    sql2pg_rowcount := 0;
    RAISE NOTICE 'Vendors cannot be deleted. They can only be marked as not active.';
    sql2pg_rowcount := 0;
    IF (CASE WHEN sql2pg_rolled_back THEN 0 ELSE 1 END > 0) THEN
        sql2pg_rolled_back := true;
    END IF;
    IF sql2pg_rolled_back THEN
        RAISE EXCEPTION 'transaction rolled back by trigger %', TG_NAME;
    END IF;
    PERFORM set_config('sql2pg.instead_of_dvendor', '', true);
    RETURN NULL;
END;
$$;
CREATE TRIGGER dvendor BEFORE DELETE ON public.vendor
FOR EACH ROW
EXECUTE FUNCTION public.dvendor();

-- sql2pg:object Trigger public.ivendor status=ConvertedWithWarnings
CREATE FUNCTION public.ivendor()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF current_setting('sql2pg.instead_of_ivendor', true) = 'on' THEN
        RETURN NEW;
    END IF;
    PERFORM set_config('sql2pg.instead_of_ivendor', 'on', true);
    INSERT INTO public.vendor (vendorid, name, activeflag)
    SELECT vendorid, upper(name), true
    FROM (SELECT new.*) AS inserted;
    PERFORM set_config('sql2pg.instead_of_ivendor', '', true);
    RETURN NULL;
END;
$$;
CREATE TRIGGER ivendor BEFORE INSERT ON public.vendor
FOR EACH ROW
EXECUTE FUNCTION public.ivendor();
