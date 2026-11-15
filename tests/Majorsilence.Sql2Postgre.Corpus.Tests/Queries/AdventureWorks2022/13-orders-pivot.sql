SELECT * FROM (SELECT YEAR(OrderDate) AS y, TerritoryID FROM Sales.SalesOrderHeader) s PIVOT (COUNT(TerritoryID) FOR y IN ([2011], [2012], [2013], [2014])) p
