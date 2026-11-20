-- sql2pg: phase Tables

-- sql2pg:object Table public.review status=Converted
CREATE TABLE public.review (
    id integer NOT NULL,
    product_id integer NOT NULL,
    reviewer varchar(50) NOT NULL,
    rating integer NOT NULL,
    comments varchar(3850),
    notes text,
    code varchar(800),
    tag varchar(400)
);

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ix_review_code status=ConvertedWithWarnings
CREATE INDEX ix_review_code ON public.review (rating) INCLUDE (tag);

-- sql2pg:object Index public.ix_review_notes status=ConvertedWithWarnings
CREATE INDEX ix_review_notes ON public.review (product_id);

-- sql2pg:object Index public.ix_review_product status=ConvertedWithWarnings
CREATE INDEX ix_review_product ON public.review (product_id, reviewer) INCLUDE (rating);

-- sql2pg:object Index public.ix_review_rating status=Converted
CREATE INDEX ix_review_rating ON public.review (rating) INCLUDE (reviewer, tag);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.review.review_pkey status=Converted
ALTER TABLE public.review ADD CONSTRAINT review_pkey PRIMARY KEY (id);
