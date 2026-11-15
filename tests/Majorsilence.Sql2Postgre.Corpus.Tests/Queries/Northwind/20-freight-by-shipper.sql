SELECT ShipVia, SUM(Freight) AS freight, MAX(Freight) AS biggest FROM dbo.Orders GROUP BY ShipVia
