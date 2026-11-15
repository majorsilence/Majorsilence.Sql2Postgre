SELECT state, COUNT(*) AS n FROM dbo.authors WHERE contract = 1 GROUP BY state
