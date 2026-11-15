SELECT CustomerID, CreditLimit, ISNULL(CreditLimit, 0) * 2 AS doubled FROM Sales.Customers WHERE CustomerID < 200
