SELECT TOP 10 StockItemID, SUM(Quantity) AS q FROM Sales.OrderLines GROUP BY StockItemID ORDER BY q DESC, StockItemID
