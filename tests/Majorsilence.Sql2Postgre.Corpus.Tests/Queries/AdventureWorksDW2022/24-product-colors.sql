SELECT ProductKey, EnglishProductName, ISNULL(Color, 'n/a') AS color FROM dbo.DimProduct WHERE ProductKey < 300
