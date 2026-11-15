SELECT customer_id
FROM shop.customer
EXCEPT
SELECT customer_id
FROM shop."order";

SELECT customer_id
FROM shop.customer
INTERSECT
SELECT customer_id
FROM shop."order";
