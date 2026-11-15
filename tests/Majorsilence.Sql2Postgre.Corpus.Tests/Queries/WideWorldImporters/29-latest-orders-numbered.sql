SELECT o.CustomerID, o.OrderID, ROW_NUMBER() OVER (PARTITION BY o.CustomerID ORDER BY o.OrderDate DESC, o.OrderID DESC) AS rn FROM Sales.Orders o WHERE o.CustomerID < 20
