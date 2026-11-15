SELECT YEAR(OrderDate) AS y, MONTH(OrderDate) AS m, SUM(SalesAmount) AS s FROM dbo.FactInternetSales GROUP BY YEAR(OrderDate), MONTH(OrderDate)
