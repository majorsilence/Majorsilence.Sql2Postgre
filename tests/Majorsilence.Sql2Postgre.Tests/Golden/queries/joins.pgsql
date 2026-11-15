SELECT c.name, o.order_id, l.qty
FROM shop.customer AS c LEFT JOIN shop."order" AS o ON o.customer_id = c.customer_id JOIN shop.order_line AS l ON l.order_id = o.order_id;
