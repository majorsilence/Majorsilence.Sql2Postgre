SELECT ProductID FROM Production.Product WHERE ProductID NOT IN (SELECT ProductID FROM Sales.SalesOrderDetail)
