SELECT c.CustomerID, p.FirstName, p.LastName FROM Sales.Customer c JOIN Person.Person p ON p.BusinessEntityID = c.PersonID WHERE c.CustomerID BETWEEN 11000 AND 11100
