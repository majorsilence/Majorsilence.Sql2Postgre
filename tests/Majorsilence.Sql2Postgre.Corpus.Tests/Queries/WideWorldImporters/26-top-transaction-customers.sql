WITH t AS (SELECT CustomerID, SUM(TransactionAmount) AS total FROM Sales.CustomerTransactions GROUP BY CustomerID) SELECT TOP 10 CustomerID, total FROM t ORDER BY total DESC, CustomerID
