UPDATE shop."order"
SET total = 0
WHERE "order".ctid IN (SELECT "order".ctid FROM shop."order" LIMIT 1);
