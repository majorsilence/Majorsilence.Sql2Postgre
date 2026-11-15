SELECT CustomerKey, DATEDIFF(day, DateFirstPurchase, '2014-01-01') AS days FROM dbo.DimCustomer WHERE CustomerKey < 11200
