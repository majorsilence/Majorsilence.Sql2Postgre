SELECT o.[Order Key], DATEDIFF(day, o.[Order Date Key], o.[Picked Date Key]) AS days FROM Fact.[Order] o WHERE o.[Order Key] < 2000 AND o.[Picked Date Key] IS NOT NULL
