-- sql2pg: phase Tables

-- sql2pg:object Table public.customer status=Converted
CREATE TABLE public.customer (
    customerid integer NOT NULL,
    name varchar(100) NOT NULL,
    notes text,
    balance numeric(19,4) DEFAULT 0 NOT NULL,
    isactive boolean DEFAULT true NOT NULL,
    createdat timestamp(3) DEFAULT localtimestamp NOT NULL,
    code char(3) DEFAULT 'ABC'
);
