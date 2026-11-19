SELECT customer_id
FROM shop.customer
WHERE lower(name COLLATE "default") LIKE lower('b%');

SELECT customer_id
FROM shop.customer
WHERE lower(name COLLATE "default") NOT SIMILAR TO lower('[a-m]%');

SELECT customer_id
FROM shop.customer
WHERE email LIKE '%@x%';
