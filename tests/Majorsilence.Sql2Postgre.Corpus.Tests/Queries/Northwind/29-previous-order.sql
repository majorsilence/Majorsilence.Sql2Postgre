SELECT OrderID, CustomerID, LAG(OrderDate) OVER (PARTITION BY CustomerID ORDER BY OrderDate, OrderID) AS previous FROM dbo.Orders
