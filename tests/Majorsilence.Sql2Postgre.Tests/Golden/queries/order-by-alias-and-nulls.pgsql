SELECT email AS e, name
FROM shop.customer
ORDER BY e NULLS FIRST, name DESC NULLS LAST;
