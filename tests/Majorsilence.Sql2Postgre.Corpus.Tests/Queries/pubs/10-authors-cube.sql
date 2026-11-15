SELECT state, contract, COUNT(*) AS n FROM dbo.authors GROUP BY CUBE (state, contract)
