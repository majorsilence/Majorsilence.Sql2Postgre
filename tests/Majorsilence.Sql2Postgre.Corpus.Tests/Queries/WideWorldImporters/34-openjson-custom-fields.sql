SELECT TOP (20) StockItemID, j.[key], j.[value], j.[type]
FROM Warehouse.StockItems CROSS APPLY OPENJSON(CustomFields) AS j
ORDER BY StockItemID, j.[key]