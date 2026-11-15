SELECT j.job_id, COUNT(e.emp_id) AS n FROM dbo.jobs j LEFT JOIN dbo.employee e ON e.job_id = j.job_id GROUP BY j.job_id
