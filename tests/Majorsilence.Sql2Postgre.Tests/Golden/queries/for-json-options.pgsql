SELECT CAST(string_agg(CAST(json_build_object('name', json_value_1, 'email', json_value_2) AS text), ',') AS text) AS "JSON_F52E2B61-18A1-11d1-B105-00805F49916B"
FROM (SELECT name AS json_value_1, email AS json_value_2 FROM shop.customer) AS for_json_rows;
