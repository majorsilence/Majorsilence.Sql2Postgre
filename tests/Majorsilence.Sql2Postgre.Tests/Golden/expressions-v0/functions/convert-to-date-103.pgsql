CASE WHEN v ~ '^[0-9]{8}$' THEN to_date(v, 'YYYYMMDD') WHEN v ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T' THEN CAST(CAST(v AS text) AS date) ELSE to_date(v, 'DD/MM/YYYY') END
