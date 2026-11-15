SELECT [Stock Holding Key], [Quantity On Hand], [Reorder Level] FROM Fact.[Stock Holding] WHERE [Quantity On Hand] < [Reorder Level] * 20
