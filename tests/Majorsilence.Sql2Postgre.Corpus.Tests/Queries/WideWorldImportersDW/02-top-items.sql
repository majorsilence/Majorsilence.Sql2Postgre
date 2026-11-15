SELECT TOP 10 [Stock Item Key], SUM(Quantity) AS q FROM Fact.Sale GROUP BY [Stock Item Key] ORDER BY q DESC, [Stock Item Key]
