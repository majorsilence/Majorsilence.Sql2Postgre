-- sql2pg: phase Tables

-- sql2pg:object Table public.t status=Manual
CREATE TABLE public.t (
    id integer NOT NULL,
    code varchar(10) NOT NULL
    /* sql2pg:untranslated S2P2007
    label AS (dbo.f(code))
    */
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ix_t_code status=Converted
CREATE INDEX ix_t_code ON public.t (code);

-- sql2pg:object Index public.ix_t_label status=Manual
/* sql2pg:untranslated S2P2014
INDEX ix_t_label (label)
*/

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.t.ck_label status=Manual
/* sql2pg:untranslated S2P2014
CHECK ck_label (label <> '')
*/

-- sql2pg:object Constraint public.t.pk_t status=Converted
ALTER TABLE public.t ADD CONSTRAINT pk_t PRIMARY KEY (id);
