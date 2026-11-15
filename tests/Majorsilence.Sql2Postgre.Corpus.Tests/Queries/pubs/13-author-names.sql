SELECT au_id, au_lname + ', ' + au_fname AS name, SUBSTRING(phone, 1, 3) AS area FROM dbo.authors
