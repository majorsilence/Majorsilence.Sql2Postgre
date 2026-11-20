SELECT count(*)
FROM shop.product TABLESAMPLE SYSTEM (10) REPEATABLE (42);

SELECT p.product_id
FROM shop.product AS p TABLESAMPLE SYSTEM (50);

-- untranslated
