INSERT INTO shop.product (product_id, name)
VALUES (12, 'Out')
RETURNING new.product_id, new.name;
