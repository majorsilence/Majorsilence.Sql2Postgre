SELECT sp.BusinessEntityID, sp.SalesQuota, sp.SalesYTD, NTILE(4) OVER (ORDER BY sp.SalesYTD) AS quartile FROM Sales.SalesPerson sp
