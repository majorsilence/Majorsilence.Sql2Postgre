SELECT ltrim(name, 'x') AS a, rtrim(name, 'x') AS b, btrim(name, 'x') AS c, btrim(name, 'x') AS d, btrim(name) AS e
FROM shop.product;
