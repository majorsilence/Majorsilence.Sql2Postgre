CAST(regexp_replace(to_char(d, 'YYYY-MM-DD"T"HH24:MI:SS.FF3'), '\.0+$', '') AS varchar(30))
