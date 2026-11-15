SELECT CategoryID, ProductID, ROW_NUMBER() OVER (PARTITION BY CategoryID ORDER BY UnitPrice DESC, ProductID) AS rn FROM dbo.Products
