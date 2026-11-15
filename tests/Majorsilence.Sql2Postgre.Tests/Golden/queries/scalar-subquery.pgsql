SELECT c.name, (SELECT count(*) FROM shop."order" AS o WHERE o.customer_id = c.customer_id) AS orders
FROM shop.customer AS c;
