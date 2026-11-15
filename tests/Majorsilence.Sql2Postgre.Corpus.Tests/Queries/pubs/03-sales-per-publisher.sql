SELECT p.pub_id, SUM(t.ytd_sales) AS sold FROM dbo.publishers p JOIN dbo.titles t ON t.pub_id = p.pub_id GROUP BY p.pub_id
