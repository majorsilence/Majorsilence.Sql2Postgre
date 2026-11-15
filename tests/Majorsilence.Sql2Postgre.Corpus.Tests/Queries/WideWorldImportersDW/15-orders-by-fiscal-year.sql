SELECT d.[Fiscal Year], COUNT(*) AS n FROM Fact.[Order] o JOIN Dimension.Date d ON d.Date = o.[Order Date Key] GROUP BY d.[Fiscal Year]
