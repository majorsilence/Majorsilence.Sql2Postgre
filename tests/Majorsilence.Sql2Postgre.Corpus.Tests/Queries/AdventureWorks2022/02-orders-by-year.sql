SELECT YEAR(OrderDate) AS y, COUNT(*) AS n, SUM(TotalDue) AS total FROM Sales.SalesOrderHeader GROUP BY YEAR(OrderDate)
