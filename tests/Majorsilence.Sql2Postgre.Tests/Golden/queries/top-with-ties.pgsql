SELECT customer_id, order_date
FROM shop."order"
ORDER BY customer_id NULLS FIRST
FETCH FIRST 1 ROWS WITH TIES;
