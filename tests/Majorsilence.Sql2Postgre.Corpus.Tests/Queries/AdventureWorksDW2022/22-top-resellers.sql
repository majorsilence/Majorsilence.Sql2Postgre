SELECT TOP 10 ResellerKey, SUM(SalesAmount) AS s FROM dbo.FactResellerSales GROUP BY ResellerKey ORDER BY s DESC, ResellerKey
