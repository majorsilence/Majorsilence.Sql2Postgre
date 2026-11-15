SELECT t.SalesTerritoryCountry, COUNT(*) AS n FROM dbo.FactInternetSales f JOIN dbo.DimSalesTerritory t ON t.SalesTerritoryKey = f.SalesTerritoryKey GROUP BY t.SalesTerritoryCountry
