SELECT t.title_id, t.price, t.ytd_sales FROM dbo.titles t WHERE t.price IS NOT NULL ORDER BY t.price DESC, t.title_id
