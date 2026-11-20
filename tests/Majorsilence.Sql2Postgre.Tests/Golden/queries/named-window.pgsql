SELECT product_id, sum(product_id) OVER w AS total, row_number() OVER (w ORDER BY product_id NULLS FIRST) AS rn
FROM shop.product
WINDOW w AS (PARTITION BY parent_id);

SELECT product_id, lag(name) OVER byid AS previous, count(*) OVER running AS n
FROM shop.product
WINDOW byid AS (ORDER BY product_id NULLS FIRST), running AS (byid ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW);
