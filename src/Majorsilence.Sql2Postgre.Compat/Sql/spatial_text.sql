-- requires: postgis
-- STAsText() and ToString() of a geometry (a geography cast to geometry: longitude, latitude) in
-- SQL Server's spelling: a space before each opening parenthesis and after each comma
-- (POINT (1 2), LINESTRING (0 0, 1 1)), and only x and y, as STAsText drops z and m.
CREATE OR REPLACE FUNCTION {schema}.spatial_text(g geometry) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE
RETURN regexp_replace(replace(ST_AsText(ST_Force2D(g)), ',', ', '), '([A-Z])\(', '\1 (', 'g');
