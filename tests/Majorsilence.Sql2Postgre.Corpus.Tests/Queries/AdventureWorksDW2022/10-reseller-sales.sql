SELECT ResellerKey, COUNT(*) AS lines, SUM(SalesAmount) AS sales FROM dbo.FactResellerSales GROUP BY ResellerKey HAVING COUNT(*) > 500
