using System.Text.Json;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Corpus.Tests;

/// <summary>What one migration of a corpus database did, in counts only.</summary>
public sealed record ScanResult(string Database, int PostgresVersion,
    IReadOnlyDictionary<string, int> ObjectsByStatus, IReadOnlyDictionary<string, int> DiagnosticsByCode,
    IReadOnlyList<string> ApplyFailures, IReadOnlyList<string> CopyFailures, IReadOnlyList<string> VerifyFailures,
    long RowsCopied)
{
    public bool Clean => ApplyFailures.Count == 0 && CopyFailures.Count == 0 && VerifyFailures.Count == 0;
}

/// <summary>
/// The Stage 1 measure: each public sample database is migrated into a fresh PostgreSQL database
/// (schema up to the tables, data, the rest of the schema, checksum verification). Every object
/// that is not Manual must apply, every table must copy and verify. The counts go to
/// <c>tests/corpus/results/</c> for BACKLOG.md. A positive control proves the scan can fail.
/// </summary>
[Category("Containers")]
[Category("Corpus")]
public class CompileScanTests
{
    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };

    public static IEnumerable<TestCaseData> Databases() =>
        CorpusEntry.All.Select(e => new TestCaseData(e).SetName($"CompileScan({e.Database})"));

    [TestCaseSource(nameof(Databases))]
    public async Task MigratesWithoutFailures(CorpusEntry entry)
    {
        var result = await ScanAsync(entry, Options);
        Record(result);
        Assert.Multiple(() =>
        {
            Assert.That(result.ApplyFailures, Is.Empty, "objects that are not Manual must apply");
            Assert.That(result.CopyFailures, Is.Empty, "every table must copy");
            Assert.That(result.VerifyFailures, Is.Empty, "every table must verify");
        });
    }

    /// <summary>
    /// A deliberately wrong mapping (an identity column typed as date) must be reported. If this
    /// passes silently the scan cannot see failures and its zeros mean nothing.
    /// </summary>
    [Test]
    public async Task PositiveControlIsCaught()
    {
        var entry = CorpusEntry.All.Single(e => e.Database == "Northwind");
        var broken = Options with { TypeOverrides = new Dictionary<string, string> { ["dbo.Orders.OrderID"] = "date" } };
        var result = await ScanAsync(entry, broken);
        Assert.That(result.Clean, Is.False);
        Assert.That(result.ApplyFailures.Concat(result.CopyFailures), Has.Some.Contains("orders"));
    }

    public static async Task<ScanResult> ScanAsync(CorpusEntry entry, ConversionOptions options)
    {
        string source = await CorpusSource.ConnectionStringAsync(entry);
        var read = await SqlServerCatalogReader.ReadAsync(source);
        var conversion = SchemaConverter.Convert(read.Database, options);

        await using var target = await PostgresDatabase.CreateAsync(entry.Database.ToLowerInvariant());
        var before = await PgApplier.ApplyAsync(conversion.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        var copy = await DataCopier.CopyAsync(read.Database, options, source, target.ConnectionString);
        var after = await PgApplier.ApplyAsync(conversion.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString);
        var verify = await DataVerifier.VerifyAsync(read.Database, options, source, target.ConnectionString, VerifyMode.Checksum);

        var manual = conversion.Objects.Where(o => o.Status == ObjectStatus.Manual).Select(o => o.TargetName).ToHashSet();
        var applyFailures = before.Outcomes.Concat(after.Outcomes)
            .Where(o => o.Status == ApplyStatus.Failed && !manual.Contains(o.TargetName))
            .Select(o => $"{o.Kind} {o.TargetName}: {o.SqlState} {o.Message}").ToList();

        var diagnostics = read.Diagnostics.Concat(conversion.Diagnostics).Concat(conversion.Objects.SelectMany(o => o.Diagnostics))
            .Concat(copy.Diagnostics)
            .GroupBy(d => $"{d.Code} {d.Severity}").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var statuses = conversion.Objects.GroupBy(o => $"{o.Kind} {o.Status}").OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        int.TryParse(Engines.PostgresVersion, out int pg);
        return new ScanResult(entry.Database, pg, statuses, diagnostics, applyFailures,
            copy.Tables.Where(t => t.Error is not null).Select(t => $"{t.Target}: {t.Error}").ToList(),
            verify.Tables.Where(t => !t.Passed).Select(t => $"{t.Target}: {t.SourceRows} vs {t.TargetRows} rows, checksum {(t.ChecksumMatches == true ? "ok" : "differs")}").ToList(),
            copy.Tables.Sum(t => t.Rows));
    }

    private static void Record(ScanResult result)
    {
        string dir = Path.Combine(CorpusSource.Directory, "results");
        System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{result.Database}-pg{result.PostgresVersion}.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine($"{result.Database} on PostgreSQL {result.PostgresVersion}: {result.RowsCopied} rows, "
                                  + $"{result.ApplyFailures.Count} apply / {result.CopyFailures.Count} copy / {result.VerifyFailures.Count} verify failures");
        foreach (var f in result.ApplyFailures.Concat(result.CopyFailures).Concat(result.VerifyFailures))
            TestContext.Out.WriteLine("  " + f);
    }
}
