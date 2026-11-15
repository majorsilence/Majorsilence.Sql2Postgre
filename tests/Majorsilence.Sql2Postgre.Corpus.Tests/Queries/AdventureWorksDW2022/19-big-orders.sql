SELECT SalesOrderNumber, COUNT(*) AS lines FROM dbo.FactInternetSales GROUP BY SalesOrderNumber HAVING COUNT(*) > 6
