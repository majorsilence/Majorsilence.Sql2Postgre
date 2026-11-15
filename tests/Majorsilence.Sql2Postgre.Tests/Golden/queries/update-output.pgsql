UPDATE shop."order"
SET total = total + 1
WHERE order_id = 1
RETURNING old.total AS before, new.total AS after;
