SELECT [Calendar Year], [Calendar Month Number], COUNT(*) AS days FROM Dimension.Date GROUP BY ROLLUP ([Calendar Year], [Calendar Month Number])
