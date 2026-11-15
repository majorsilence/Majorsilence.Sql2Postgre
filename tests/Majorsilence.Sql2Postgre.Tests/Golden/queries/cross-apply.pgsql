SELECT c.name, x.order_date
FROM shop.customer AS c CROSS JOIN LATERAL (SELECT o.order_date FROM shop."order" AS o WHERE o.customer_id = c.customer_id ORDER BY o.order_date DESC NULLS LAST LIMIT 1) AS x;
