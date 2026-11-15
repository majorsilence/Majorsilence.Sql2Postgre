SELECT emp_id, job_lvl, RANK() OVER (PARTITION BY job_id ORDER BY job_lvl DESC) AS r FROM dbo.employee
