SELECT customer.name, "order".order_id
FROM shop.customer JOIN shop."order" ON "order".customer_id = customer.customer_id;
