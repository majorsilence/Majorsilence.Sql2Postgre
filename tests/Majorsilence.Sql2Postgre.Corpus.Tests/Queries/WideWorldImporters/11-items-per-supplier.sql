SELECT SupplierID, COUNT(*) AS n FROM Warehouse.StockItems GROUP BY CUBE (SupplierID)
