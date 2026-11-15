SELECT od.OrderID, SUM(od.UnitPrice * od.Quantity) AS gross, SUM(od.Quantity) AS qty FROM dbo.[Order Details] od GROUP BY od.OrderID HAVING SUM(od.Quantity) > 100
