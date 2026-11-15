-- sql2pg: phase Schemas

-- sql2pg:object Schema app status=Converted
CREATE SCHEMA IF NOT EXISTS app;

-- sql2pg:object Schema sales status=Converted
CREATE SCHEMA IF NOT EXISTS sales;

-- sql2pg: phase Tables

-- sql2pg:object Table app.lookup status=Converted
CREATE TABLE app.lookup (
    id integer NOT NULL
);

-- sql2pg:object Table sales.orders status=Converted
CREATE TABLE sales.orders (
    id integer NOT NULL
);
