SELECT c.ColorName, COUNT(si.StockItemID) AS items FROM Warehouse.Colors c LEFT JOIN Warehouse.StockItems si ON si.ColorID = c.ColorID GROUP BY c.ColorName
