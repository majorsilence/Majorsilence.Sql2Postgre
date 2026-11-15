SELECT order_id, row_number() OVER (PARTITION BY customer_id ORDER BY order_date NULLS FIRST) AS rn
FROM shop."order";
