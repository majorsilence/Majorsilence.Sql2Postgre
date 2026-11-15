SELECT StockItemID, JSON_VALUE(CustomFields, '$.CountryOfManufacture') AS country FROM Warehouse.StockItems
