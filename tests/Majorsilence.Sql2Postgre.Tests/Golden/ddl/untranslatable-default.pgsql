-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Manual
CREATE TABLE public.t (
    id integer NOT NULL,
    d varchar(20),
    c integer
    /* sql2pg:untranslated S2P3001
    d DEFAULT (dbo.f())
    */
);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.t.t_check status=Manual
/* sql2pg:untranslated S2P3001
ALTER TABLE dbo.t ADD CONSTRAINT t_check CHECK (dbo.g(c) = 1)
*/
