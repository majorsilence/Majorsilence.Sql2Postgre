SELECT CustomerID, UPPER(CompanyName) AS u, LEN(CompanyName) AS l, LEFT(ContactName, 3) AS c3 FROM dbo.Customers
