SELECT BusinessType, COUNT(*) AS n, AVG(AnnualRevenue) AS revenue FROM dbo.DimReseller GROUP BY BusinessType
