SELECT s.SupplierID, (SELECT COUNT(*) FROM dbo.Products p WHERE p.SupplierID = s.SupplierID) AS products FROM dbo.Suppliers s
