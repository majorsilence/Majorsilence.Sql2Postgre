SELECT c.name, x.n
FROM shop.customer AS c LEFT JOIN LATERAL (SELECT count(*) AS n FROM shop."order" AS o WHERE o.customer_id = c.customer_id) AS x ON true;
