WITH s AS (SELECT SalespersonPersonID, COUNT(*) AS n FROM Sales.Orders GROUP BY SalespersonPersonID) SELECT SalespersonPersonID, n, RANK() OVER (ORDER BY n DESC) AS r FROM s
