CREATE TEMP TABLE c AS
SELECT customer_id, name
FROM shop.customer;

SELECT name
FROM c;
