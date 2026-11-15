MERGE INTO shop.product AS t
USING (VALUES (2, 'Child A2'), (20, 'Fresh')) AS s (id, name)
ON t.product_id = s.id
WHEN MATCHED THEN UPDATE SET name = s.name
WHEN NOT MATCHED THEN INSERT (product_id, name) VALUES (s.id, s.name)
WHEN NOT MATCHED BY SOURCE AND t.product_id = 5 THEN DELETE
RETURNING merge_action(), new.product_id;
