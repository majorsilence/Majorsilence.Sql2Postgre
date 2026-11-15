using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Data;

[Category("Containers")]
public class DataCopierTests
{
    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };

    /// <summary>Edge values for every type in shop.all_types (ids 1–6 in insert order).</summary>
    private const string EdgeRows = """
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

    private SqlServerDatabase _source = null!;
    private SourceDatabase _db = null!;

    [OneTimeSetUp]
    public async Task CreateSource()
    {
        _source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + EdgeRows, "copy");
        _db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
    }

    [OneTimeTearDown]
    public async Task DropSource()
    {
        if (_source is not null)
            await _source.DisposeAsync();
    }

    /// <summary>Creates the target and applies everything up to the tables (constraints come after the data).</summary>
    private async Task<PostgresDatabase> TargetAsync()
    {
        var target = await PostgresDatabase.CreateAsync("copy");
        var schema = SchemaConverter.Convert(_db, Options);
        var report = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        Assert.That(report.Failed, Is.Zero, string.Join("\n", report.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => o.Message)));
        return target;
    }

    [Test]
    public async Task EveryValueArrivesAsTransformed()
    {
        await using var target = await TargetAsync();
        var result = await DataCopier.CopyAsync(_db, Options, _source.ConnectionString, target.ConnectionString);
        Assert.That(result.Succeeded, Is.True, string.Join("; ", result.Tables.Select(t => t.Error).Where(e => e is not null)));

        // What the source rows should become: the same SELECT and transforms the copier uses.
        var translator = new SchemaTranslator(Options);
        var plan = TablePlan.Build(_db, translator.Translate(_db), translator.Names).Single(p => p.Source.Name.Name == "all_types");
        var expected = await ReadSourceAsync(plan);
        var actual = await ReadTargetAsync(plan, target.ConnectionString);

        Assert.That(actual, Has.Count.EqualTo(6));
        Assert.Multiple(() =>
        {
            for (int row = 0; row < expected.Count; row++)
                for (int col = 0; col < plan.Columns.Count; col++)
                    Assert.That(actual[row][col], Is.EqualTo(expected[row][col]), $"row {row + 1}, {plan.Columns[col].Source.Name}");
        });

        int Col(string name) => plan.Columns.ToList().FindIndex(c => c.Source.Name == name);
        Assert.Multiple(() =>
        {
            Assert.That(actual[3][Col("c_nvarchar")], Is.EqualTo("ab"), "NUL removed");
            Assert.That(actual[3][Col("c_nvarchar_max")], Is.EqualTo(""), "a lone NUL becomes empty");
            Assert.That(actual[4][Col("c_datetime2")], Is.EqualTo("2024-01-01T00:00:00.000000"), ".0000005 rounds to even (down)");
            Assert.That(actual[5][Col("c_datetime2")], Is.EqualTo("2024-01-01T00:00:00.000002"), ".0000015 rounds to even (up)");
            Assert.That(actual[5][Col("c_time")], Is.EqualTo("00:00:00.000002"));
            Assert.That(actual[5][Col("c_datetimeoffset")], Is.EqualTo("2024-01-01T17:30:00.000002Z"), "offset applied, stored as UTC");
            Assert.That(actual[2][Col("c_hierarchyid")], Is.EqualTo("/1/2/3/"));
            Assert.That(actual[2][Col("c_nvarchar")], Is.EqualTo("日本語 😀"));
            Assert.That(actual[2][Col("c_varbinary_max")].Length, Is.EqualTo(2 * 10485760), "10 MB blob");
            Assert.That(actual[0].Skip(1).All(v => v == CanonicalValue.Null || plan.Columns[actual[0].IndexOf(v)].Source.Name == "c_rowversion"), Is.True, "row 1 is NULL apart from id and rowversion");
        });

        var nulWarnings = result.Tables.Single(t => t.Table.Name == "all_types").Diagnostics.Where(d => d.Code == DiagnosticCodes.NulCharacterRemoved).ToList();
        Assert.That(nulWarnings, Has.Count.EqualTo(3), "one per column that had NULs");
    }

    [Test]
    public async Task IdentitiesAndSequencesContinue()
    {
        await using var target = await TargetAsync();
        await DataCopier.CopyAsync(_db, Options, _source.ConnectionString, target.ConnectionString);
        await using var pg = new NpgsqlConnection(target.ConnectionString);
        await pg.OpenAsync();

        async Task<long> ScalarAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, pg);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Multiple(async () =>
        {
            // customer: IDENTITY(100, 5), two rows copied (100, 105), so the next is 110.
            Assert.That(await ScalarAsync("INSERT INTO shop.customer (customercode, name, created_at, is_active, row_guid) VALUES ('C3', 'Three', now(), true, gen_random_uuid()) RETURNING customer_id"), Is.EqualTo(110));
            // order: empty, so it starts at its seed.
            Assert.That(await ScalarAsync("INSERT INTO shop.\"order\" (customer_id, order_date, total) VALUES (100, current_date, 0) RETURNING order_id"), Is.EqualTo(1));
            // invoice_no handed out 1000 in SQL Server; the next is 1010.
            Assert.That(await ScalarAsync("SELECT nextval('shop.invoice_no')"), Is.EqualTo(1010));
        });
    }

    [Test]
    public async Task SplitCopyMatchesSingleStreamAndResumes()
    {
        await using var source = await SqlServerDatabase.CreateAsync("""
            CREATE TABLE dbo.big (id int NOT NULL PRIMARY KEY, name nvarchar(20) NOT NULL);
            GO
            WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO dbo.big (id, name) SELECT i * 3, CONCAT(N'row ', i) FROM n;
            """, "big");
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        await using var target = await PostgresDatabase.CreateAsync("big");
        await PgApplier.ApplyAsync(SchemaConverter.Convert(db, Options).Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);

        string state = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"state-{Guid.NewGuid():N}.json");
        var split = new DataCopyOptions { SplitThresholdRows = 1000, MaxParallelTables = 4, StatePath = state };
        var first = await DataCopier.CopyAsync(db, Options, source.ConnectionString, target.ConnectionString, split);
        var again = await DataCopier.CopyAsync(db, Options, source.ConnectionString, target.ConnectionString, split);
        var fresh = await DataCopier.CopyAsync(db, Options, source.ConnectionString, target.ConnectionString, split with { StatePath = null });

        const string Sums = "count(*), sum(id), sum(length(name))";
        Assert.Multiple(() =>
        {
            Assert.That(first.Tables.Single().Rows, Is.EqualTo(100000));
            Assert.That(again.Tables.Single().Rows, Is.Zero, "everything was recorded as done");
            Assert.That(fresh.Tables.Single().Rows, Is.EqualTo(100000), "without state each range is cleared and recopied");
            Assert.That(File.ReadAllText(state), Does.Contain("dbo.big#4/4"));
        });
        await using var pg = new NpgsqlConnection(target.ConnectionString);
        await pg.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {Sums} FROM public.big", pg);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.That(new[] { reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) },
            Is.EqualTo(new long[] { 100000, 3L * 100000 * 100001 / 2, Enumerable.Range(1, 100000).Sum(i => (long)("row " + i).Length) }));
        File.Delete(state);
    }

    private async Task<List<List<string>>> ReadSourceAsync(TablePlan plan)
    {
        var rows = new List<List<string>>();
        var states = plan.Columns.Select(_ => new ColumnState()).ToArray();
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(plan.SelectSql() + " ORDER BY [id]", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new List<string>();
            for (int i = 0, ordinal = 0; i < plan.Columns.Count; ordinal += plan.Columns[i].SourceSelect.Count, i++)
                row.Add(CanonicalValue.Format(plan.Columns[i].Read(reader, ordinal, states[i]), plan.Columns[i].PgType));
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<List<List<string>>> ReadTargetAsync(TablePlan plan, string connection)
    {
        var rows = new List<List<string>>();
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {string.Join(", ", plan.Columns.Select(c => NameMapper.Quote(c.TargetName)))} FROM {plan.Target.Qualified} ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => CanonicalValue.Format(reader.GetValue(i), reader.GetDataTypeName(i))).ToList());
        return rows;
    }
}
