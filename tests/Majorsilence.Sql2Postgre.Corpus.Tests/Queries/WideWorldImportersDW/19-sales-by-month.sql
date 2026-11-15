SELECT DATEPART(month, [Invoice Date Key]) AS m, COUNT(*) AS n FROM Fact.Sale GROUP BY DATEPART(month, [Invoice Date Key])
