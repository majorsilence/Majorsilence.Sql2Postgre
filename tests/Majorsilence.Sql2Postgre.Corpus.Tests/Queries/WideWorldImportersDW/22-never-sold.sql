SELECT [Stock Item Key] FROM Dimension.[Stock Item] si WHERE NOT EXISTS (SELECT 1 FROM Fact.Sale s WHERE s.[Stock Item Key] = si.[Stock Item Key])
