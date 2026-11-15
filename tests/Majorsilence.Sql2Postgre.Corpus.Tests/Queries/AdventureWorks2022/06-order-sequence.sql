SELECT CustomerID, SalesOrderID, ROW_NUMBER() OVER (PARTITION BY CustomerID ORDER BY OrderDate, SalesOrderID) AS seq FROM Sales.SalesOrderHeader WHERE CustomerID < 11100
