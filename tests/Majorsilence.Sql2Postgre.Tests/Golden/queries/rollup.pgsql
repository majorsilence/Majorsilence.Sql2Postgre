SELECT customer_id, CAST(extract(year FROM CAST(order_date AS timestamp)) AS integer) AS y, count(*) AS n
FROM shop."order"
GROUP BY ROLLUP (customer_id, CAST(extract(year FROM CAST(order_date AS timestamp)) AS integer));
