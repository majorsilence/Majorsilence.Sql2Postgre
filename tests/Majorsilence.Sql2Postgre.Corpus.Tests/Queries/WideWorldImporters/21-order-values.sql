SELECT o.OrderID, SUM(ol.Quantity * ol.UnitPrice) AS value FROM Sales.Orders o JOIN Sales.OrderLines ol ON ol.OrderID = o.OrderID WHERE o.OrderID < 300 GROUP BY o.OrderID
