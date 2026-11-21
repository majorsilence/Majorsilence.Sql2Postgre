SELECT CAST(mssql_compat.try_convert_iso8601(CAST(name AS text)) AS timestamp(3)) AS a, CAST(mssql_compat.try_convert_iso8601(CAST('2013-01-02T07:05:00' AS text)) AS date) AS b, CAST(to_char(CAST('2013-01-02' AS date), 'YYYYMMDD') AS varchar(10)) AS c, CAST(mssql_compat.try_cast_integer(CAST(name AS text)) AS integer) AS d
FROM shop.product;

-- untranslated

SELECT CAST(mssql_compat.try_convert_iso8601(CAST(CAST(name AS json) #>> '{when}' AS text)) AS timestamp(6)) AS f, CAST(to_char(CAST('2013-01-02' AS date), 'YYYYMMDD') AS varchar(8)) AS g
FROM shop.product;
