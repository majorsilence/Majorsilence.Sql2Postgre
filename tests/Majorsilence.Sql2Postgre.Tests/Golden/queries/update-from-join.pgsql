UPDATE shop."order" AS o
SET total = 1
FROM shop.customer AS c
WHERE c.customer_id = o.customer_id AND c.name = 'Alice';
