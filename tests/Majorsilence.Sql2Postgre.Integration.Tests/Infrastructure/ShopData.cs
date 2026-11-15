namespace Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

/// <summary>Rows for the shop fixture that exercise the data copy.</summary>
public static class ShopData
{
    /// <summary>Edge values for every type in shop.all_types (ids 1–6 in insert order).</summary>
    public const string EdgeRows = """
        INSERT INTO shop.all_types DEFAULT VALUES;  -- 1: all NULL
        INSERT INTO shop.all_types (c_bit, c_tinyint, c_smallint, c_int, c_bigint, c_decimal, c_numeric, c_money, c_smallmoney,
            c_float, c_float24, c_real, c_char, c_varchar, c_varchar_max, c_nchar, c_nvarchar, c_nvarchar_max, c_text, c_ntext,
            c_binary, c_varbinary, c_varbinary_max, c_image, c_date, c_time, c_time3, c_datetime, c_smalldatetime, c_datetime2,
            c_datetime2_3, c_datetimeoffset, c_uniqueidentifier, c_xml, c_sysname, c_sql_variant, c_hierarchyid, c_geography, c_geometry, c_phone)
        VALUES (0, 0, -32768, -2147483648, -9223372036854775808, -99999999999999.9999, -99999999.99, -922337203685477.5808, -214748.3648,
            -1.79E+308, -3.4E+38, -3.4E+38, '', '', '', N'', N'', N'', '', N'',
            0x00, 0x, 0x, 0x00, '0001-01-01', '00:00:00', '00:00:00', '1753-01-01', '1900-01-01', '0001-01-01',
            '0001-01-01', '0001-01-02 00:00:00 +14:00', '00000000-0000-0000-0000-000000000000', N'', N'', 0, '/', NULL, NULL, N'');  -- 2: minimums
        INSERT INTO shop.all_types (c_bit, c_tinyint, c_smallint, c_int, c_bigint, c_decimal, c_numeric, c_money, c_smallmoney,
            c_float, c_float24, c_real, c_char, c_varchar, c_varchar_max, c_nchar, c_nvarchar, c_nvarchar_max, c_text, c_ntext,
            c_binary, c_varbinary, c_varbinary_max, c_image, c_date, c_time, c_time3, c_datetime, c_smalldatetime, c_datetime2,
            c_datetime2_3, c_datetimeoffset, c_uniqueidentifier, c_xml, c_sysname, c_sql_variant, c_hierarchyid, c_geography, c_geometry, c_phone)
        VALUES (1, 255, 32767, 2147483647, 9223372036854775807, 99999999999999.9999, 99999999.99, 922337203685477.5807, 214748.3647,
            1.79E+308, 3.4E+38, 3.4E+38, REPLICATE('x', 10), REPLICATE('y', 50), REPLICATE(CAST('z' AS varchar(max)), 100000),
            N'ÄÖÜ', N'日本語 😀', REPLICATE(CAST(N'é' AS nvarchar(max)), 50000), 'text', N'ntext ✓',
            0xFFFFFFFFFFFFFFFF, 0x0102030405060708090A0B0C0D0E0F10, CAST(REPLICATE(CAST(0xAB AS varbinary(max)), 10485760) AS varbinary(max)), 0xDEADBEEF,
            '9999-12-31', '23:59:59.9999990', '23:59:59.999', '9999-12-31 23:59:59.997', '2079-06-06 23:59', '9999-12-31 23:59:59.9999990',
            '9999-12-31 23:59:59.999', '9999-12-30 23:59:59.9999990 +14:00', 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF', N'<a b="1">x &amp; y</a>',
            N'sysname', N'variant', '/1/2/3/', geography::Point(47.65, -122.34, 4326), geometry::STGeomFromText('LINESTRING(0 0, 1 1)', 0), N'+1 555 0100');  -- 3: maximums
        INSERT INTO shop.all_types (c_nvarchar, c_varchar, c_nvarchar_max) VALUES (N'a' + NCHAR(0) + N'b', 'c' + CHAR(0), NCHAR(0));  -- 4: NULs
        INSERT INTO shop.all_types (c_datetime2, c_time, c_datetimeoffset) VALUES ('2024-01-01 00:00:00.0000005', '00:00:00.0000005', '2024-01-01 00:00:00.0000005 +00:00');  -- 5: half, rounds to even (down)
        INSERT INTO shop.all_types (c_datetime2, c_time, c_datetimeoffset) VALUES ('2024-01-01 00:00:00.0000015', '00:00:00.0000015', '2024-01-01 12:00:00.0000015 -05:30');  -- 6: half, rounds to even (up)
        INSERT INTO shop.customer (CustomerCode, name, email) VALUES ('C1', N'One', N'a@b'), ('C2', N'Two', NULL);
        DECLARE @n bigint = NEXT VALUE FOR shop.invoice_no;
        """;
}
