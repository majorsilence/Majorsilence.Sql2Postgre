SELECT CustomerKey, x.orders FROM dbo.DimCustomer c CROSS APPLY (SELECT COUNT(*) AS orders FROM dbo.FactInternetSales f WHERE f.CustomerKey = c.CustomerKey) x WHERE c.CustomerKey < 11100
