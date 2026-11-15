SELECT i.InvoiceID, SUM(il.ExtendedPrice) AS total FROM Sales.Invoices i JOIN Sales.InvoiceLines il ON il.InvoiceID = i.InvoiceID WHERE i.InvoiceID < 500 GROUP BY i.InvoiceID
