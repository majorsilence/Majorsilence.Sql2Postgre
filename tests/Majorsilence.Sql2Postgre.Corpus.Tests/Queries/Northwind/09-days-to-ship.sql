SELECT OrderID, DATEDIFF(day, OrderDate, ShippedDate) AS days FROM dbo.Orders WHERE ShippedDate IS NOT NULL
