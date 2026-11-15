SELECT d.CalendarYear, SUM(f.SalesAmount) AS sales FROM dbo.FactInternetSales f JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey GROUP BY d.CalendarYear
