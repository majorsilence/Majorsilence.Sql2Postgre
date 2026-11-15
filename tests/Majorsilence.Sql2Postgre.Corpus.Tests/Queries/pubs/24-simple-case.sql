SELECT title_id, CASE type WHEN 'business' THEN 'B' WHEN 'psychology' THEN 'P' ELSE 'O' END AS t FROM dbo.titles
