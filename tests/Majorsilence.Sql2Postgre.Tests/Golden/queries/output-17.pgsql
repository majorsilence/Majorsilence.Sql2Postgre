INSERT INTO shop.product (product_id, name)
VALUES (12, 'Out')
RETURNING product.product_id, product.name;

DELETE FROM shop.sales_fact
WHERE amount > 15
RETURNING sales_fact.*;

UPDATE shop."order"
SET total = total + 1
FROM (SELECT sql2pg_old AS sql2pg_row, sql2pg_old.order_id AS sql2pg_k1 FROM shop."order" AS sql2pg_old) AS sql2pg_old
WHERE sql2pg_old.sql2pg_k1 = "order".order_id AND order_id = 1
RETURNING (sql2pg_old.sql2pg_row).total AS before, "order".total AS after;

UPDATE shop."order" AS o
SET total = 0
FROM shop.customer AS c, (SELECT sql2pg_old AS sql2pg_row, sql2pg_old.order_id AS sql2pg_k1 FROM shop."order" AS sql2pg_old) AS sql2pg_old
WHERE sql2pg_old.sql2pg_k1 = o.order_id AND c.customer_id = o.customer_id AND c.name = 'x'
RETURNING (sql2pg_old.sql2pg_row).total, o.*;

UPDATE shop.sales_fact
SET amount = amount * 2
FROM (SELECT sql2pg_old AS sql2pg_row, sql2pg_old.ctid AS sql2pg_k1 FROM shop.sales_fact AS sql2pg_old) AS sql2pg_old
WHERE sql2pg_old.sql2pg_k1 = sales_fact.ctid AND sale_id = 3
RETURNING (sql2pg_old.sql2pg_row).amount, sales_fact.amount;

MERGE INTO shop.product AS t
USING (VALUES (2, 'Child A2'), (20, 'Fresh')) AS s (id, name)
ON t.product_id = s.id
WHEN MATCHED THEN UPDATE SET name = s.name
WHEN NOT MATCHED THEN INSERT (product_id, name) VALUES (s.id, s.name)
WHEN NOT MATCHED BY SOURCE AND t.product_id = 5 THEN DELETE
RETURNING merge_action(), CASE WHEN merge_action() = 'DELETE' THEN NULL ELSE t.product_id END AS product_id;

MERGE INTO shop.product AS t
USING (VALUES (20, 'Fresh')) AS s (id, name)
ON t.product_id = s.id
WHEN NOT MATCHED THEN INSERT (product_id, name) VALUES (s.id, s.name)
WHEN NOT MATCHED BY SOURCE AND t.product_id = 5 THEN DELETE
RETURNING merge_action(), CASE WHEN merge_action() = 'INSERT' THEN NULL ELSE t.product_id END AS product_id, CASE WHEN merge_action() = 'DELETE' THEN NULL ELSE t.product_id END AS product_id, CASE WHEN merge_action() = 'DELETE' THEN NULL ELSE t.name END AS name, CASE WHEN merge_action() = 'DELETE' THEN NULL ELSE t.parent_id END AS parent_id;

-- untranslated
