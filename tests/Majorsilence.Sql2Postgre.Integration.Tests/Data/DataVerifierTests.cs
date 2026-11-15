using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Data;

[Category("Containers")]
public class DataVerifierTests
{
    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };

    private SqlServerDatabase _source = null!;
    private SourceDatabase _db = null!;

    [OneTimeSetUp]
    public async Task CreateSource()
    {
        _source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + ShopData.EdgeRows, "verify");
        _db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
    }

    [OneTimeTearDown]
    public async Task DropSource()
    {
        if (_source is not null)
            await _source.DisposeAsync();
    }

    private async Task<PostgresDatabase> CopiedAsync()
    {
        var target = await PostgresDatabase.CreateAsync("verify");
        await PgApplier.ApplyAsync(SchemaConverter.Convert(_db, Options).Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        var copy = await DataCopier.CopyAsync(_db, Options, _source.ConnectionString, target.ConnectionString);
        Assert.That(copy.Succeeded, Is.True);
        return target;
    }

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task EqualTablesVerify()
    {
        await using var target = await CopiedAsync();
        var counts = await DataVerifier.VerifyAsync(_db, Options, _source.ConnectionString, target.ConnectionString, VerifyMode.Count);
        var checksums = await DataVerifier.VerifyAsync(_db, Options, _source.ConnectionString, target.ConnectionString, VerifyMode.Checksum);
        Assert.Multiple(() =>
        {
            Assert.That(counts.Passed, Is.True);
            Assert.That(checksums.Tables.Where(t => !t.Passed).Select(t => t.Table.ToString()), Is.Empty);
            Assert.That(checksums.Tables.Single(t => t.Table.Name == "all_types").SourceRows, Is.EqualTo(6));
            Assert.That(checksums.Tables.All(t => t.ChecksumMatches == true), Is.True);
        });
    }

    [Test]
    public async Task ChangedValueFailsTheChecksumOnly()
    {
        await using var target = await CopiedAsync();
        await ExecuteAsync(target.ConnectionString, "UPDATE shop.all_types SET c_int = c_int + 1 WHERE id = 2");
        var counts = await DataVerifier.VerifyAsync(_db, Options, _source.ConnectionString, target.ConnectionString, VerifyMode.Count, ["all_types"]);
        var checksums = await DataVerifier.VerifyAsync(_db, Options, _source.ConnectionString, target.ConnectionString, VerifyMode.Checksum, ["shop.all_types"]);
        var table = checksums.Tables.Single();
        Assert.Multiple(() =>
        {
            Assert.That(counts.Passed, Is.True, "counts still agree");
            Assert.That(table.ChecksumMatches, Is.False);
            Assert.That(table.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.ChecksumMismatch));
        });
    }

    [Test]
    public async Task DeletedRowFailsTheCount()
    {
        await using var target = await CopiedAsync();
        await ExecuteAsync(target.ConnectionString, "DELETE FROM shop.customer WHERE customercode = 'C2'");
        var counts = await DataVerifier.VerifyAsync(_db, Options, _source.ConnectionString, target.ConnectionString, VerifyMode.Count, ["customer"]);
        var table = counts.Tables.Single();
        Assert.Multiple(() =>
        {
            Assert.That(table.SourceRows, Is.EqualTo(2));
            Assert.That(table.TargetRows, Is.EqualTo(1));
            Assert.That(table.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.RowCountMismatch));
            Assert.That(counts.Passed, Is.False);
        });
    }

    [Test]
    public void RowHashDependsOnEveryValueAndItsPosition()
    {
        ulong a = DataVerifier.RowHash(["1", "x"]);
        Assert.Multiple(() =>
        {
            Assert.That(DataVerifier.RowHash(["1", "x"]), Is.EqualTo(a));
            Assert.That(DataVerifier.RowHash(["x", "1"]), Is.Not.EqualTo(a));
            Assert.That(DataVerifier.RowHash(["1x", ""]), Is.Not.EqualTo(a), "the separator keeps fields apart");
        });
    }
}
