SELECT r.RegionDescription, COUNT(*) AS n FROM dbo.Territories t JOIN dbo.Region r ON r.RegionID = t.RegionID GROUP BY r.RegionDescription
