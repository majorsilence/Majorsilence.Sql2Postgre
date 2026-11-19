UPDATE shop."order" AS o
SET total = 1
WHERE o.ctid IN (SELECT o.ctid FROM shop."order" AS o LEFT JOIN shop.customer AS c ON c.customer_id = o.customer_id);
