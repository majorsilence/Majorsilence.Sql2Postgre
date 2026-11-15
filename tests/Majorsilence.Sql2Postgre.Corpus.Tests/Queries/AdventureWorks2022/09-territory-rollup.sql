SELECT TerritoryID, SUM(SubTotal) AS subtotal FROM Sales.SalesOrderHeader GROUP BY ROLLUP (TerritoryID)
