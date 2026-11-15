SELECT TransactionTypeID, COUNT(*) AS n, SUM(TransactionAmount) AS amount FROM Sales.CustomerTransactions GROUP BY TransactionTypeID
