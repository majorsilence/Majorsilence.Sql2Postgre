-- sql2pg: phase Tables

-- sql2pg:object Table public.city status=ConvertedWithWarnings
CREATE TABLE public.city (
    cityid integer NOT NULL,
    location bytea
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object View public.citypoints status=Manual
/* sql2pg:untranslated S2P3030
CREATE VIEW dbo.CityPoints AS
SELECT CityId, Location.Lat AS Latitude, Location.STAsText() AS Wkt FROM dbo.City;
*/

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.city.city_pkey status=Converted
ALTER TABLE public.city ADD CONSTRAINT city_pkey PRIMARY KEY (cityid);
