SELECT *
FROM (SELECT customer_id, count(y) FILTER (WHERE y = 2023) AS "2023", count(y) FILTER (WHERE y = 2024) AS "2024" FROM (SELECT customer_id, CAST(extract(year FROM CAST(order_date AS timestamp)) AS integer) AS y FROM shop."order") AS s GROUP BY customer_id) AS p;
