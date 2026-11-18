SELECT TOP (20) StockItemID, JSON_MODIFY(CustomFields, N'append $.Tags', N'Converted') AS Fields
FROM Warehouse.StockItems
ORDER BY StockItemID