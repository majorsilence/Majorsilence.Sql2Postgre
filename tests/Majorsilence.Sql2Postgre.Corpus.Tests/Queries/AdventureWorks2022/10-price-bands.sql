SELECT ProductID, CASE WHEN ListPrice = 0 THEN 'free' WHEN ListPrice < 100 THEN 'cheap' ELSE 'dear' END AS band FROM Production.Product
