SELECT st.stor_id, COUNT(DISTINCT s.ord_num) AS orders FROM dbo.stores st LEFT JOIN dbo.sales s ON s.stor_id = st.stor_id GROUP BY st.stor_id
