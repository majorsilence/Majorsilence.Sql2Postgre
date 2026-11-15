SELECT SalesTerritoryKey, COUNT(DISTINCT CustomerKey) AS customers FROM dbo.FactInternetSales GROUP BY SalesTerritoryKey
