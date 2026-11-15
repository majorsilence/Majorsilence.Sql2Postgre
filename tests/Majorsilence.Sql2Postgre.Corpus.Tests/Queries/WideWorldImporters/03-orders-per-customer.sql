SELECT c.CustomerID, COUNT(o.OrderID) AS orders FROM Sales.Customers c LEFT JOIN Sales.Orders o ON o.CustomerID = c.CustomerID GROUP BY c.CustomerID
