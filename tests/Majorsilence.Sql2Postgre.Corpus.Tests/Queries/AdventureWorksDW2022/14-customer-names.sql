SELECT CustomerKey, FirstName + ' ' + LastName AS name, MaritalStatus FROM dbo.DimCustomer WHERE CustomerKey < 11200
