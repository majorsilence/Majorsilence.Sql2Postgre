SELECT OrderID, DATEADD(day, 30, OrderDate) AS due, EOMONTH(OrderDate) AS month_end FROM dbo.Orders WHERE OrderID < 10350
