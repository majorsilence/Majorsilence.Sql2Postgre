SELECT ProductID, CASE WHEN UnitsInStock = 0 THEN 'out' WHEN UnitsInStock < ReorderLevel THEN 'low' ELSE 'ok' END AS stock FROM dbo.Products
