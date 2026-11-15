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


    private SqlServerDatabase _source = null!;
    private SourceDatabase _db = null!;

    [OneTimeSetUp]
    public async Task CreateSource()
    {
        _source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + ShopData.EdgeRows, "copy");
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
