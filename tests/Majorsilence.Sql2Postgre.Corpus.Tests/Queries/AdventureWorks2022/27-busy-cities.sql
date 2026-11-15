SELECT a.City, COUNT(*) AS n FROM Person.Address a GROUP BY a.City HAVING COUNT(*) > 100
