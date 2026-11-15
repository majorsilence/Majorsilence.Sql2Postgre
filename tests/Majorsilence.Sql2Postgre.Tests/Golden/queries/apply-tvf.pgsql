SELECT c.name, x.order_date
FROM shop.customer AS c CROSS JOIN LATERAL shop.customer_orders(c.customer_id) AS x;
