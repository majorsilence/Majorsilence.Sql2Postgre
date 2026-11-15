SELECT CustomerID, OrderID, COUNT(*) OVER (PARTITION BY CustomerID ORDER BY OrderID) AS running FROM dbo.Orders
