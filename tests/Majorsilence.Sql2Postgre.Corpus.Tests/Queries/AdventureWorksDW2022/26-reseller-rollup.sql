SELECT d.CalendarYear, d.CalendarQuarter, SUM(f.SalesAmount) AS s FROM dbo.FactResellerSales f JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey GROUP BY ROLLUP (d.CalendarYear, d.CalendarQuarter)
