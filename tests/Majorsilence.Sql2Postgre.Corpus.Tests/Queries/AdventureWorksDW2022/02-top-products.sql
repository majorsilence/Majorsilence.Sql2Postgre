SELECT TOP 5 ProductKey, SUM(OrderQuantity) AS q FROM dbo.FactInternetSales GROUP BY ProductKey ORDER BY q DESC, ProductKey
