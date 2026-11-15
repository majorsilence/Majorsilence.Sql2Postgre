SELECT order_id
FROM shop."order"
ORDER BY order_id NULLS FIRST
LIMIT (SELECT ceil(count(*) * 50 / 100.0) FROM (SELECT order_id FROM shop."order") AS counted);
