SELECT emp_id, fname + ' ' + lname AS name, hire_date FROM dbo.employee WHERE YEAR(hire_date) = 1990
