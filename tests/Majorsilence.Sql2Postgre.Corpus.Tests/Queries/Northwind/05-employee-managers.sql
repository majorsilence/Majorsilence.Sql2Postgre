SELECT e.EmployeeID, e.LastName + N', ' + e.FirstName AS name, m.LastName AS manager FROM dbo.Employees e LEFT JOIN dbo.Employees m ON m.EmployeeID = e.ReportsTo
