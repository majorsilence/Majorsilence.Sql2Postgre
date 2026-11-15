WITH p AS (SELECT [Salesperson Key], SUM(Profit) AS profit FROM Fact.Sale GROUP BY [Salesperson Key]) SELECT [Salesperson Key], profit, DENSE_RANK() OVER (ORDER BY profit DESC) AS r FROM p
