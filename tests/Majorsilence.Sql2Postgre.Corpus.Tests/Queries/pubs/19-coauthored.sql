SELECT ta.title_id, COUNT(*) AS authors FROM dbo.titleauthor ta GROUP BY ta.title_id HAVING COUNT(*) > 1
