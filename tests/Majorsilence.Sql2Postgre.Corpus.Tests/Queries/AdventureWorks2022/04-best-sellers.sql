SELECT d.ProductID, SUM(d.OrderQty) AS qty, SUM(d.LineTotal) AS total FROM Sales.SalesOrderDetail d GROUP BY d.ProductID HAVING SUM(d.OrderQty) > 1000
