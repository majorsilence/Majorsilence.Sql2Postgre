SELECT name
FROM shop.customer AS c
WHERE EXISTS (SELECT 1 FROM shop."order" AS o WHERE o.customer_id = c.customer_id) AND c.customer_id IN (SELECT customer_id FROM shop."order") AND c.customer_id > ANY (SELECT 0);
