SELECT product_id, last_value(parent_id) IGNORE NULLS OVER (ORDER BY product_id NULLS FIRST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS last_parent, lag(parent_id) OVER (ORDER BY product_id NULLS FIRST) AS previous_parent
FROM shop.product;

SELECT product_id, first_value(parent_id) IGNORE NULLS OVER (ORDER BY product_id DESC NULLS LAST) AS first_parent, lead(parent_id, 1) IGNORE NULLS OVER (ORDER BY product_id NULLS FIRST) AS next_parent
FROM shop.product;

SELECT product_id, CASE WHEN lead(true, 2, false) OVER (PARTITION BY name ORDER BY product_id NULLS FIRST) THEN first_value(parent_id) IGNORE NULLS OVER (PARTITION BY name ORDER BY product_id NULLS FIRST ROWS BETWEEN 2 FOLLOWING AND UNBOUNDED FOLLOWING) ELSE 0 END AS second_parent, CASE WHEN lag(true, 1, false) OVER (ORDER BY product_id NULLS FIRST) THEN last_value(parent_id) IGNORE NULLS OVER (ORDER BY product_id NULLS FIRST ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) ELSE -1 END AS previous_parent
FROM shop.product;
