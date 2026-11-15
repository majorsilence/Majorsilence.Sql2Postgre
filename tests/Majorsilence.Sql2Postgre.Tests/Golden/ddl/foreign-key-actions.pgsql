-- sql2pg: phase Tables

-- sql2pg:object Table public.child status=Converted
CREATE TABLE public.child (
    id integer NOT NULL,
    parent_id integer,
    code integer,
    other integer
);

-- sql2pg:object Table public.parent status=Converted
CREATE TABLE public.parent (
    id integer NOT NULL,
    code integer NOT NULL
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.parent.parent_code_key status=Converted
ALTER TABLE public.parent ADD CONSTRAINT parent_code_key UNIQUE (code);

-- sql2pg:object Constraint public.parent.parent_pkey status=Converted
ALTER TABLE public.parent ADD CONSTRAINT parent_pkey PRIMARY KEY (id);

-- sql2pg: phase ForeignKeys

-- sql2pg:object Constraint public.child.child_parent_id_fkey status=Converted
ALTER TABLE public.child ADD CONSTRAINT child_parent_id_fkey FOREIGN KEY (parent_id) REFERENCES public.parent (id) ON DELETE CASCADE ON UPDATE CASCADE;

-- sql2pg:object Constraint public.child.fk_child_code status=Converted
ALTER TABLE public.child ADD CONSTRAINT fk_child_code FOREIGN KEY (code) REFERENCES public.parent (code) ON DELETE SET NULL;

-- sql2pg:object Constraint public.child.fk_child_other status=Converted
ALTER TABLE public.child ADD CONSTRAINT fk_child_other FOREIGN KEY (other) REFERENCES public.parent (id) ON DELETE SET DEFAULT;
