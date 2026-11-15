SELECT name
FROM shop.customer AS c
WHERE NOT EXISTS (SELECT 1 FROM shop."order" AS o WHERE o.customer_id = c.customer_id) AND c.customer_id >= ALL (SELECT customer_id FROM shop.customer);
