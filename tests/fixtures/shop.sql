-- A made-up schema that exercises everything the Stage 1 reader and DDL translator handle.
-- Shared by the catalog reader (WP 1.2), the script reader (WP 1.3) and the DDL tests (WP 1.8).
CREATE SCHEMA shop;
GO
CREATE TYPE shop.phone FROM nvarchar(20) NULL;
GO
CREATE TYPE shop.line_list AS TABLE (product_id int NOT NULL, qty int NOT NULL);
GO
CREATE SEQUENCE shop.invoice_no AS bigint START WITH 1000 INCREMENT BY 10 MINVALUE 1000 MAXVALUE 999999 NO CYCLE;
GO
CREATE TABLE shop.all_types (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT pk_all_types PRIMARY KEY CLUSTERED,
    c_bit bit NULL,
    c_tinyint tinyint NULL,
    c_smallint smallint NULL,
    c_int int NULL,
    c_bigint bigint NULL,
    c_decimal decimal(18,4) NULL,
    c_numeric numeric(10,2) NULL,
    c_money money NULL,
    c_smallmoney smallmoney NULL,
    c_float float NULL,
    c_float24 float(24) NULL,
    c_real real NULL,
    c_char char(10) NULL,
    c_varchar varchar(50) NULL,
    c_varchar_max varchar(max) NULL,
    c_nchar nchar(10) NULL,
    c_nvarchar nvarchar(50) NULL,
    c_nvarchar_max nvarchar(max) NULL,
    c_text text NULL,
    c_ntext ntext NULL,
    c_binary binary(8) NULL,
    c_varbinary varbinary(16) NULL,
    c_varbinary_max varbinary(max) NULL,
    c_image image NULL,
    c_date date NULL,
    c_time time(7) NULL,
    c_time3 time(3) NULL,
    c_datetime datetime NULL,
    c_smalldatetime smalldatetime NULL,
    c_datetime2 datetime2(7) NULL,
    c_datetime2_3 datetime2(3) NULL,
    c_datetimeoffset datetimeoffset(7) NULL,
    c_uniqueidentifier uniqueidentifier NULL,
    c_xml xml NULL,
    c_sysname sysname NULL,
    c_rowversion rowversion NOT NULL,
    c_sql_variant sql_variant NULL,
    c_hierarchyid hierarchyid NULL,
    c_geography geography NULL,
    c_geometry geometry NULL,
    c_phone shop.phone NULL
);
GO
CREATE TABLE shop.customer (
    customer_id int IDENTITY(100, 5) NOT NULL,
    CustomerCode varchar(20) NOT NULL,
    name nvarchar(100) COLLATE Latin1_General_CI_AS NOT NULL,
    email nvarchar(200) NULL,
    created_at datetime2(3) NOT NULL CONSTRAINT df_customer_created DEFAULT (sysdatetime()),
    is_active bit NOT NULL DEFAULT ((1)),
    row_guid uniqueidentifier ROWGUIDCOL NOT NULL DEFAULT (newsequentialid()),
    notes nvarchar(4000) SPARSE NULL,
    CONSTRAINT pk_customer PRIMARY KEY NONCLUSTERED (customer_id),
    CONSTRAINT uq_customer_code UNIQUE (CustomerCode),
    CONSTRAINT ck_customer_email CHECK (email LIKE '%@%')
);
GO
CREATE CLUSTERED INDEX ix_customer_name ON shop.customer (name);
GO
CREATE INDEX ix_customer_email_active ON shop.customer (email DESC) INCLUDE (name) WHERE is_active = 1;
GO
ALTER TABLE shop.customer NOCHECK CONSTRAINT ck_customer_email;
GO
CREATE TABLE shop.[order] (
    order_id bigint IDENTITY NOT NULL PRIMARY KEY,
    customer_id int NOT NULL,
    order_date date NOT NULL,
    total money NOT NULL CONSTRAINT df_order_total DEFAULT 0,
    CONSTRAINT fk_order_customer FOREIGN KEY (customer_id) REFERENCES shop.customer (customer_id) ON DELETE CASCADE
);
GO
CREATE TABLE shop.product (
    product_id int NOT NULL CONSTRAINT pk_product PRIMARY KEY,
    name nvarchar(100) NOT NULL,
    parent_id int NULL
);
GO
ALTER TABLE shop.product ADD CONSTRAINT fk_product_parent FOREIGN KEY (parent_id) REFERENCES shop.product (product_id);
GO
CREATE TABLE shop.order_line (
    order_id bigint NOT NULL,
    line_no int NOT NULL,
    product_id int NULL,
    qty int NOT NULL,
    unit_price decimal(10,2) NOT NULL,
    line_total AS (qty * unit_price) PERSISTED,
    line_tax AS (qty * unit_price * 0.1),
    CONSTRAINT pk_order_line PRIMARY KEY (order_id, line_no),
    CONSTRAINT fk_line_order FOREIGN KEY (order_id) REFERENCES shop.[order] (order_id) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT ck_line_qty CHECK (qty > 0)
);
GO
ALTER TABLE shop.order_line WITH NOCHECK ADD CONSTRAINT fk_line_product FOREIGN KEY (product_id) REFERENCES shop.product (product_id) ON DELETE SET NULL;
GO
CREATE TABLE shop.sales_fact (
    sale_id int NOT NULL,
    amount decimal(12,2) NOT NULL
);
GO
CREATE CLUSTERED COLUMNSTORE INDEX cci_sales_fact ON shop.sales_fact;
GO
CREATE VIEW shop.active_customer AS
SELECT customer_id, name FROM shop.customer WHERE is_active = 1;
GO
CREATE FUNCTION shop.line_amount (@qty int, @price decimal(10,2))
RETURNS decimal(12,2)
AS
BEGIN
    RETURN @qty * @price;
END;
GO
CREATE FUNCTION shop.customer_orders (@customer_id int)
RETURNS TABLE
AS
RETURN (SELECT order_id, order_date FROM shop.[order] WHERE customer_id = @customer_id);
GO
CREATE PROCEDURE shop.add_order @customer_id int, @order_id bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO shop.[order] (customer_id, order_date) VALUES (@customer_id, CAST(GETDATE() AS date));
    SET @order_id = SCOPE_IDENTITY();
END;
GO
CREATE TRIGGER shop.trg_order_line_total ON shop.order_line AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE o SET total = (SELECT SUM(l.line_total) FROM shop.order_line l WHERE l.order_id = o.order_id)
    FROM shop.[order] o
    WHERE o.order_id IN (SELECT order_id FROM inserted);
END;
GO
CREATE PROCEDURE shop.secret_proc WITH ENCRYPTION
AS
SELECT 1 AS one;
GO
