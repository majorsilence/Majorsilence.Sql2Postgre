SELECT customer_id, order_date, count(*)
FROM shop."order"
GROUP BY GROUPING SETS ((customer_id), (order_date), ());
