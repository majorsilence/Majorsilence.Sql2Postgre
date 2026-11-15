SELECT e.BusinessEntityID, e.JobTitle, DATEDIFF(year, e.BirthDate, '2024-01-01') AS age FROM HumanResources.Employee e
