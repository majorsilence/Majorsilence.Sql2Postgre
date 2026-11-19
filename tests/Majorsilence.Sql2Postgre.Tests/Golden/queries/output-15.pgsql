INSERT INTO shop.product (product_id, name)
VALUES (12, 'Out')
RETURNING product.*;

DELETE FROM shop.product AS p
USING shop.product AS parent
WHERE parent.product_id = p.parent_id AND parent.name = 'x'
RETURNING p.product_id;

UPDATE shop."order"
SET total = total + 1
FROM (SELECT sql2pg_old AS sql2pg_row, sql2pg_old.order_id AS sql2pg_k1 FROM shop."order" AS sql2pg_old) AS sql2pg_old
WHERE sql2pg_old.sql2pg_k1 = "order".order_id AND order_id = 1
RETURNING (sql2pg_old.sql2pg_row).*;

-- untranslated
