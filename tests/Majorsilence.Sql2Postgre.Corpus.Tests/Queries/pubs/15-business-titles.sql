SELECT p.pub_name, t.title FROM dbo.publishers p JOIN dbo.titles t ON t.pub_id = p.pub_id WHERE t.type = 'business'
