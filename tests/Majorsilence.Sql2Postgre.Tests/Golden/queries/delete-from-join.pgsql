DELETE FROM shop.order_line AS l
USING shop."order" AS o
WHERE o.order_id = l.order_id AND o.customer_id = 100;
