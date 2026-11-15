SELECT YEAR(InvoiceDate) AS y, DATEPART(quarter, InvoiceDate) AS q, COUNT(*) AS n FROM Sales.Invoices GROUP BY YEAR(InvoiceDate), DATEPART(quarter, InvoiceDate)
