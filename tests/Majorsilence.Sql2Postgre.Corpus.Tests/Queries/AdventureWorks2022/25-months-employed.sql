SELECT e.BusinessEntityID, DATEDIFF(month, e.HireDate, '2024-01-01') AS months FROM HumanResources.Employee e
