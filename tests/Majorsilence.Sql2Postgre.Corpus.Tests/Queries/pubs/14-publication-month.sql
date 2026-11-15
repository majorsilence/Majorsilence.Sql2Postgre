SELECT title_id, DATENAME(month, pubdate) AS m, YEAR(pubdate) AS y FROM dbo.titles
