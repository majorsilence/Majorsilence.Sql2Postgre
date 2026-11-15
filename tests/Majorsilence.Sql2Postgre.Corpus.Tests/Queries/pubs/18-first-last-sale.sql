SELECT stor_id, MIN(ord_date) AS first_sale, MAX(ord_date) AS last_sale FROM dbo.sales GROUP BY stor_id
