SELECT ord_num, title_id, DATEPART(quarter, ord_date) AS q FROM dbo.sales
