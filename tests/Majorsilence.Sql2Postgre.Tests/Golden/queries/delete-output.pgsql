DELETE FROM shop.sales_fact
WHERE amount > 15
RETURNING old.*;
