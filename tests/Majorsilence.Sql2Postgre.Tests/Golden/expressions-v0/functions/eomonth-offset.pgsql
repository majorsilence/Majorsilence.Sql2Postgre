CAST(date_trunc('month', CAST(dt AS timestamp)) + (-1 + 1) * interval '1 month' - interval '1 day' AS date)
