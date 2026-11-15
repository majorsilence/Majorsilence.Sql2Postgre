SELECT c.CustomerID, COUNT(o.OrderID) AS orders FROM dbo.Customers c LEFT JOIN dbo.Orders o ON o.CustomerID = c.CustomerID GROUP BY c.CustomerID
