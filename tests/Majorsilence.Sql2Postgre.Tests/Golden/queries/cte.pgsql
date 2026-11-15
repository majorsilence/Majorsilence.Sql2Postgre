WITH recent AS (SELECT customer_id, max(order_date) AS last_order FROM shop."order" GROUP BY customer_id)
SELECT c.name, r.last_order
FROM shop.customer AS c JOIN recent AS r ON r.customer_id = c.customer_id;
