SELECT s.[Sale Key], s.Quantity * s.[Unit Price] AS gross FROM Fact.Sale s WHERE s.[Sale Key] < 1000
