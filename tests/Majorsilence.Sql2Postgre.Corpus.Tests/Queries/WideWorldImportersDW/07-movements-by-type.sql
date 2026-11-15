SELECT [Transaction Type Key], COUNT(*) AS n, SUM(Quantity) AS q FROM Fact.Movement GROUP BY [Transaction Type Key]
