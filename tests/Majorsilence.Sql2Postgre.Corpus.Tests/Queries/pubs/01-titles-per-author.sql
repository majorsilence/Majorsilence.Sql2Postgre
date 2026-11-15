SELECT a.au_id, COUNT(ta.title_id) AS titles FROM dbo.authors a LEFT JOIN dbo.titleauthor ta ON ta.au_id = a.au_id GROUP BY a.au_id
