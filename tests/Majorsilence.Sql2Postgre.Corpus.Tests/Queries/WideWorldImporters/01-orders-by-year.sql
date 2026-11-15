SELECT YEAR(OrderDate) AS y, COUNT(*) AS n FROM Sales.Orders GROUP BY YEAR(OrderDate)
