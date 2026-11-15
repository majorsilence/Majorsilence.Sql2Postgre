SELECT d.[Calendar Year], SUM(s.[Total Including Tax]) AS total FROM Fact.Sale s JOIN Dimension.Date d ON d.Date = s.[Invoice Date Key] GROUP BY d.[Calendar Year]
