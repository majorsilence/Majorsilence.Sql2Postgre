SELECT g.EnglishCountryRegionName, COUNT(*) AS customers FROM dbo.DimCustomer c JOIN dbo.DimGeography g ON g.GeographyKey = c.GeographyKey GROUP BY g.EnglishCountryRegionName
