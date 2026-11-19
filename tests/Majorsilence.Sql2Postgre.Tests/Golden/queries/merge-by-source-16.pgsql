WITH sql2pg_by_source_1 AS (DELETE FROM shop.product AS t
WHERE NOT EXISTS (SELECT 1 FROM (VALUES (2, 'Child A2'), (20, 'Fresh')) AS s (id, name) WHERE t.product_id = s.id) AND t.product_id = 5)
MERGE INTO shop.product AS t
USING (VALUES (2, 'Child A2'), (20, 'Fresh')) AS s (id, name)
ON t.product_id = s.id
WHEN MATCHED THEN UPDATE SET name = s.name
WHEN NOT MATCHED THEN INSERT (product_id, name) VALUES (s.id, s.name);

WITH sql2pg_by_source_1 AS (UPDATE shop.product
SET name = 'orphan'
WHERE NOT EXISTS (SELECT 1 FROM shop.customer AS c WHERE product.product_id = c.customer_id) AND product.parent_id IS NULL)
DELETE FROM shop.product
WHERE NOT EXISTS (SELECT 1 FROM shop.customer AS c WHERE product.product_id = c.customer_id) AND NOT COALESCE(product.parent_id IS NULL, false);
