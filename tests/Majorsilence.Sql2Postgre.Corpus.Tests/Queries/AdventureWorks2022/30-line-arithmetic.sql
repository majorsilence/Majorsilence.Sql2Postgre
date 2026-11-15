SELECT d.SalesOrderID, d.ProductID, d.UnitPrice * d.OrderQty * (1 - d.UnitPriceDiscount) AS line FROM Sales.SalesOrderDetail d WHERE d.SalesOrderID < 43700
