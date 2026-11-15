SELECT CustomerID, ISNULL(Region, N'-') AS region, COALESCE(Fax, Phone) AS contact FROM dbo.Customers
