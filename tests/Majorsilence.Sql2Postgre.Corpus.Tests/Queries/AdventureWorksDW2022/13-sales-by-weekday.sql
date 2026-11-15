SELECT d.EnglishDayNameOfWeek, COUNT(*) AS n FROM dbo.FactInternetSales f JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey GROUP BY d.EnglishDayNameOfWeek
