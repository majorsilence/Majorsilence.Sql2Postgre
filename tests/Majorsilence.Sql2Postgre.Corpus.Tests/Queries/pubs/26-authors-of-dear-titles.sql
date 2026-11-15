SELECT au_id FROM dbo.authors a WHERE EXISTS (SELECT 1 FROM dbo.titleauthor ta JOIN dbo.titles t ON t.title_id = ta.title_id WHERE ta.au_id = a.au_id AND t.price > 20)
