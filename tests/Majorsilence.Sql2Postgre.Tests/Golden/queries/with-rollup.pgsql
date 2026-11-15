SELECT customer_id, count(*) AS n
FROM shop."order"
GROUP BY ROLLUP (customer_id);
