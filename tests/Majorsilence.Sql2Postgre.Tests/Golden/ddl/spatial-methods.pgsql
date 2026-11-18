-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg:object Extension postgis status=Converted
CREATE EXTENSION IF NOT EXISTS postgis;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.spatial_text status=Converted
-- requires: postgis
-- STAsText() and ToString() of a geometry (a geography cast to geometry: longitude, latitude) in
-- SQL Server's spelling: a space before each opening parenthesis and after each comma
-- (POINT (1 2), LINESTRING (0 0, 1 1)), and only x and y, as STAsText drops z and m.
CREATE OR REPLACE FUNCTION mssql_compat.spatial_text(g geometry) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE
RETURN regexp_replace(replace(ST_AsText(ST_Force2D(g)), ',', ', '), '([A-Z])\(', '\1 (', 'g');

-- sql2pg: phase Tables

-- sql2pg:object Table public.city status=Converted
CREATE TABLE public.city (
    cityid integer NOT NULL,
    name varchar(50) NOT NULL,
    location geography
);

-- sql2pg:object Table public.lot status=Converted
CREATE TABLE public.lot (
    lotid integer NOT NULL,
    shape geometry
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object View public.citypoints status=Converted
CREATE VIEW public.citypoints AS
SELECT cityid, name, ST_Y(CAST(location AS geometry)) AS latitude, ST_X(CAST(c.location AS geometry)) AS longitude, mssql_compat.spatial_text(CAST(location AS geometry)) AS wkt, ST_SRID(location) AS srid
FROM public.city AS c;

-- sql2pg:object View public.lotfacts status=Converted
CREATE VIEW public.lotfacts AS
SELECT lotid, ST_Area(shape) AS area, ST_Length(shape) + ST_Perimeter(shape) AS perimeter, ST_X(CASE WHEN ST_Dimension(shape) = 2 THEN ST_Centroid(shape) END) AS centrex, ST_Y(CASE WHEN ST_Dimension(shape) = 2 THEN ST_Centroid(shape) END) AS centrey, substr(ST_GeometryType(shape), 4) AS kind, ST_NPoints(shape) AS points, ST_IsValid(shape) AS valid, mssql_compat.spatial_text(ST_ForcePolygonCCW(ST_Envelope(shape))) AS box
FROM public.lot;

-- sql2pg:object Procedure public.nearby status=ConvertedWithWarnings
CREATE PROCEDURE public.nearby(lat double precision, long double precision, metres double precision, INOUT result_set_1 refcursor DEFAULT NULL, INOUT result_set_2 refcursor DEFAULT NULL, INOUT result_set_3 refcursor DEFAULT NULL)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_here geography;
BEGIN
    v_here := CAST(ST_SetSRID(ST_MakePoint(nearby.long, nearby.lat), 4326) AS geography);
    OPEN result_set_1 FOR SELECT cityid, name, ST_Distance(location, v_here) AS metres
        FROM public.city
        WHERE ST_Distance(location, v_here) <= nearby.metres AND ST_Intersects(location, ST_Buffer(v_here, nearby.metres, 'quad_segs=32')) = true
        ORDER BY metres NULLS FIRST;
    OPEN result_set_2 FOR SELECT l.lotid
        FROM public.lot AS l
        WHERE ST_Contains(l.shape, ST_GeomFromText('POINT(1 1)', 0)) = true OR ST_Intersects(l.shape, ST_GeomFromText('LINESTRING(0 0, 5 5)', 0)) = true;
    OPEN result_set_3 FOR SELECT ST_X(CAST(v_here AS geometry)) AS x, ST_AsBinary(v_here) AS wkb, ST_Covers(CAST(ST_GeomFromText('POLYGON((-123 45, -121 45, -121 48, -123 48, -123 45))', 4326) AS geography), v_here) AS inside;
END;
$$;

-- sql2pg: phase Indexes

-- sql2pg:object Index public.six_city_location status=Converted
CREATE INDEX six_city_location ON public.city USING gist (location);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.city.city_pkey status=Converted
ALTER TABLE public.city ADD CONSTRAINT city_pkey PRIMARY KEY (cityid);

-- sql2pg:object Constraint public.lot.lot_pkey status=Converted
ALTER TABLE public.lot ADD CONSTRAINT lot_pkey PRIMARY KEY (lotid);
