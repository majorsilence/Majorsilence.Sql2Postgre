SELECT TOP 10 [Customer Key], SUM(Profit) AS profit FROM Fact.Sale GROUP BY [Customer Key] ORDER BY profit DESC, [Customer Key]
