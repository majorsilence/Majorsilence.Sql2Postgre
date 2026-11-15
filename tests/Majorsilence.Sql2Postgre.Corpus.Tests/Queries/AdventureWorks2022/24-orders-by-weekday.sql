SELECT DATEPART(weekday, OrderDate) AS wd, COUNT(*) AS n FROM Sales.SalesOrderHeader GROUP BY DATEPART(weekday, OrderDate)
