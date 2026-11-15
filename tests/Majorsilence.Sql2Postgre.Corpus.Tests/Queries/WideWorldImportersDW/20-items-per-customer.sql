SELECT [Customer Key], COUNT(DISTINCT [Stock Item Key]) AS items FROM Fact.Sale GROUP BY [Customer Key]
