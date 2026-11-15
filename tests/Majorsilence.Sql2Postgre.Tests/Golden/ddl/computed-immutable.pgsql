-- sql2pg: phase Tables

-- sql2pg:object Table public.person status=Manual
CREATE TABLE public.person (
    id integer NOT NULL,
    first_name varchar(50) NOT NULL,
    last_name varchar(50),
    age integer,
    born timestamp(0),
    custom text,
    search_name text GENERATED ALWAYS AS (COALESCE(first_name, '') || ' ' || COALESCE(last_name, '') || ' ' || COALESCE(CAST(age AS text), '')) VIRTUAL,
    tags text GENERATED ALWAYS AS (CAST(CAST(custom AS json) #> '{tags}' AS text)) VIRTUAL
    /* sql2pg:untranslated S2P2009
    born_text AS (CONCAT(first_name, born))
    */
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.person.person_pkey status=Converted
ALTER TABLE public.person ADD CONSTRAINT person_pkey PRIMARY KEY (id);
