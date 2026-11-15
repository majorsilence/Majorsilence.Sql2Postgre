-- sql2pg: phase Sequences

-- sql2pg:object Sequence public.s_dec status=Converted
CREATE SEQUENCE public.s_dec AS bigint START WITH 10 INCREMENT BY 10 MINVALUE 10 MAXVALUE 1000000 NO CYCLE;

-- sql2pg:object Sequence public.s_down status=Converted
CREATE SEQUENCE public.s_down AS bigint START WITH -1 INCREMENT BY -5 MINVALUE -9223372036854775808 MAXVALUE -1 CYCLE;

-- sql2pg:object Sequence public.s_int status=Converted
CREATE SEQUENCE public.s_int AS integer START WITH 1 INCREMENT BY 1 MINVALUE -2147483648 MAXVALUE 2147483647 NO CYCLE;
