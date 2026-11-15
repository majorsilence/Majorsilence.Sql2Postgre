SELECT SalesOrderID, CONVERT(varchar(10), OrderDate, 112) AS d FROM Sales.SalesOrderHeader WHERE SalesOrderID < 43700
