SELECT customer_id, count(*) AS n, sum(total) AS total
FROM shop."order"
GROUP BY customer_id
HAVING count(*) > 1;
