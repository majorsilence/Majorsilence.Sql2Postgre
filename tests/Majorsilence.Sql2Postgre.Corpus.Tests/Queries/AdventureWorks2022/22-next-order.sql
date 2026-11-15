SELECT h.SalesOrderID, h.OrderDate, LEAD(h.OrderDate) OVER (PARTITION BY h.CustomerID ORDER BY h.OrderDate, h.SalesOrderID) AS next_order FROM Sales.SalesOrderHeader h WHERE h.CustomerID < 11050
