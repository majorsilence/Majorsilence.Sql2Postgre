SELECT YEAR(OrderDate) AS y, MONTH(OrderDate) AS m, COUNT(*) AS n FROM dbo.Orders GROUP BY YEAR(OrderDate), MONTH(OrderDate)
