SELECT CustomerKey, DATEDIFF(year, BirthDate, '2014-01-01') AS age FROM dbo.DimCustomer WHERE CustomerKey < 11100
