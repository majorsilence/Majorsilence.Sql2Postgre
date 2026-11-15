CAST(trunc(avg(n) OVER (PARTITION BY s)) AS integer)
