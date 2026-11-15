SELECT o.OrderID, DATEDIFF(day, o.OrderDate, o.ExpectedDeliveryDate) AS days FROM Sales.Orders o WHERE o.OrderID < 2000
