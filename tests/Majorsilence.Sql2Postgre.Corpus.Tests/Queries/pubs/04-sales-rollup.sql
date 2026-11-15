SELECT stor_id, SUM(qty) AS q FROM dbo.sales GROUP BY stor_id WITH ROLLUP
