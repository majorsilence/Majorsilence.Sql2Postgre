SELECT c.CustomerID, x.OrderID FROM Sales.Customers c CROSS APPLY (SELECT TOP 1 o.OrderID FROM Sales.Orders o WHERE o.CustomerID = c.CustomerID ORDER BY o.OrderDate DESC, o.OrderID DESC) x
