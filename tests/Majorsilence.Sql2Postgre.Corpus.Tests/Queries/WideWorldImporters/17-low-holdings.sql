SELECT StockItemID, QuantityOnHand, ReorderLevel FROM Warehouse.StockItemHoldings WHERE QuantityOnHand < ReorderLevel * 10
