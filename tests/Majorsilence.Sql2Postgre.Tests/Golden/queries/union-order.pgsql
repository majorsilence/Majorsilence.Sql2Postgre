SELECT name
FROM shop.customer
UNION
SELECT name
FROM shop.product
ORDER BY name NULLS FIRST;
