SELECT CONVERT(varchar(10), OrderDate, 120) AS d, COUNT(*) AS n FROM dbo.Orders GROUP BY CONVERT(varchar(10), OrderDate, 120)
