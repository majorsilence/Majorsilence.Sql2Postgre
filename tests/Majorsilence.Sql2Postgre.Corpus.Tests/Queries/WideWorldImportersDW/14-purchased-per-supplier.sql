SELECT [Supplier Key], SUM([Ordered Quantity]) AS q FROM Fact.Purchase GROUP BY [Supplier Key]
