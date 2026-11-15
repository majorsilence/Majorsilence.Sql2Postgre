SELECT c.CustomerID FROM dbo.Customers c WHERE NOT EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.CustomerID = c.CustomerID)
