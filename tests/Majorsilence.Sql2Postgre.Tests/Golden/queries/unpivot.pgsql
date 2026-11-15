SELECT sale_id, k, v
FROM (SELECT s.sale_id, unpivoted_pairs.k, unpivoted_pairs.v FROM (SELECT sale_id, amount AS a, CAST(sale_id AS numeric(12,2)) AS b FROM shop.sales_fact) AS s CROSS JOIN LATERAL (VALUES ('a', s.a), ('b', s.b)) AS unpivoted_pairs (k, v) WHERE unpivoted_pairs.v IS NOT NULL) AS u;
