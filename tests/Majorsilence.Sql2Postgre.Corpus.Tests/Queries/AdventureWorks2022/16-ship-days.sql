SELECT SalesOrderID, DATEDIFF(day, OrderDate, ShipDate) AS days FROM Sales.SalesOrderHeader WHERE SalesOrderID < 44000
