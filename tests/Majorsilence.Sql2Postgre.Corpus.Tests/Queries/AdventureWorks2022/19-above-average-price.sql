SELECT p.Name, p.ListPrice FROM Production.Product p WHERE p.ListPrice > (SELECT AVG(ListPrice) FROM Production.Product)
