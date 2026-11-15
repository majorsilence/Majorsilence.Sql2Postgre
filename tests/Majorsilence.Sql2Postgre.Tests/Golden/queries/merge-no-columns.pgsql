MERGE INTO shop.sales_fact AS t
USING shop.product AS s
ON t.sale_id = s.product_id
WHEN NOT MATCHED THEN INSERT (sale_id, amount) VALUES (s.product_id, 0);
