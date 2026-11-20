SELECT replace(regexp_replace(CAST(to_json(name) AS text), '^"|"$', '', 'g'), '/', '\/') AS escaped
FROM shop.product;

SELECT CAST(to_char(order_date, 'Mon DD, YYYY') AS varchar(30)) AS us, CAST(to_char(order_date, 'DD Mon YYYY') AS varchar(20)) AS uk, CAST(to_char(order_date, 'Mon DD, YY') AS varchar(12)) AS us2
FROM shop."order";

SELECT CAST(current_setting('sql2pg.session_salesterritory', true) AS varchar(50)) AS territory;
