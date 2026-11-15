UPDATE shop.order_line
SET qty = qty + 1, unit_price = unit_price * 2
WHERE order_id = 1;
