SELECT c.CustomerID, x.n FROM Sales.Customer c CROSS APPLY (SELECT COUNT(*) AS n FROM Sales.SalesOrderHeader h WHERE h.CustomerID = c.CustomerID) x WHERE c.CustomerID < 11200
