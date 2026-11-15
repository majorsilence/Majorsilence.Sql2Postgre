SELECT StockItemID, UnitPrice, CASE WHEN UnitPrice < 10 THEN 'low' WHEN UnitPrice < 100 THEN 'mid' ELSE 'high' END AS band FROM Warehouse.StockItems
