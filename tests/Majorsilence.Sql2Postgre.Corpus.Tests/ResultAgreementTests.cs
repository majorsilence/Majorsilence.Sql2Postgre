using System.Collections.Concurrent;
using System.Text.Json;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Corpus.Tests;

/// <summary>How far one query's PostgreSQL translation agreed with SQL Server.</summary>
/// <param name="Score">Matching rows / rows × 100 (100 when both are empty); 0 when the columns differ or a side fails.</param>
public sealed record AgreementResult(string Query, double Score, int SqlServerRows, int PostgresRows, string? Problem);

/// <summary>
/// The Stage 2 measure: queries written for this repo against the corpus schemas
/// (<c>Queries/&lt;database&gt;/*.sql</c>) are translated and run on the SQL Server corpus database
/// and on its PostgreSQL migration. Each scores matching rows / rows × 100, compared under the
/// <see cref="BothEngines"/> rules (canonical values; multisets unless the query has a top-level
/// ORDER BY). <c>Queries/Baselines.json</c> holds the score each query is expected to reach: any
/// drop fails, and so does a rise of more than 5, until the baseline is updated with a reason
/// (<c>SQL2PG_UPDATE_BASELINES=1</c> rewrites the file from the run).
/// </summary>
[Category("Containers")]
[Category("Corpus")]
public class ResultAgreementTests
{
    private const double AllowedRise = 5;
    private static readonly ConcurrentDictionary<string, AgreementResult> Results = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<SemanticModel>> Models = new(StringComparer.OrdinalIgnoreCase);

    private static string QueriesDirectory => Path.Combine(AppContext.BaseDirectory, "Queries");

    private static string BaselinesSource => Path.Combine(CorpusSource.Directory, "..", "Majorsilence.Sql2Postgre.Corpus.Tests", "Queries", "Baselines.json");

    private static readonly Lazy<IReadOnlyDictionary<string, double>> Baselines = new(() =>
    {
        string path = Path.Combine(QueriesDirectory, "Baselines.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<SortedDictionary<string, double>>(File.ReadAllText(path)) ?? new SortedDictionary<string, double>()
            : new SortedDictionary<string, double>();
    });

    private static bool Updating => Environment.GetEnvironmentVariable("SQL2PG_UPDATE_BASELINES") == "1";

    public static IEnumerable<TestCaseData> Queries()
    {
        foreach (var entry in CorpusEntry.All)
        {
            string dir = Path.Combine(QueriesDirectory, entry.Database);
            if (!Directory.Exists(dir))
                continue;
            foreach (string file in Directory.GetFiles(dir, "*.sql").Order(StringComparer.Ordinal))
            {
                string key = $"{entry.Database}/{Path.GetFileNameWithoutExtension(file)}";
                yield return new TestCaseData(entry, key, file).SetName($"Agreement({key})");
            }
        }
    }

    [TestCaseSource(nameof(Queries))]
    public async Task MeetsItsBaseline(CorpusEntry entry, string key, string file)
    {
        var migrated = await CorpusMigration.DefaultAsync(entry);
        var result = await ScoreAsync(migrated, key, await File.ReadAllTextAsync(file));
        Results[key] = result;
        TestContext.Out.WriteLine($"{key}: {result.Score} ({result.SqlServerRows} vs {result.PostgresRows} rows){(result.Problem is null ? "" : " — " + result.Problem)}");
        if (Updating)
            return;

        Assert.That(Baselines.Value.TryGetValue(key, out double baseline), Is.True,
            $"{key} has no baseline; run with SQL2PG_UPDATE_BASELINES=1 and record why in the PR");
        Assert.Multiple(() =>
        {
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(baseline), $"{key} fell below its baseline {baseline}: {result.Problem}");
            Assert.That(result.Score, Is.LessThanOrEqualTo(baseline + AllowedRise), $"{key} rose above its baseline {baseline}: update Baselines.json");
        });
    }

    [OneTimeTearDown]
    public void WriteResults()
    {
        if (Results.IsEmpty)
            return;
        string dir = Path.Combine(CorpusSource.Directory, "results");
        Directory.CreateDirectory(dir);
        var ordered = Results.Values.OrderBy(r => r.Query, StringComparer.Ordinal).ToList();
        var json = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(dir, $"agreement-pg{Engines.PostgresVersion}.json"), JsonSerializer.Serialize(ordered, json));
        if (Updating)
        {
            var merged = new SortedDictionary<string, double>(Baselines.Value.ToDictionary(), StringComparer.Ordinal);
            foreach (var r in ordered)
                merged[r.Query] = r.Score;
            File.WriteAllText(Path.GetFullPath(BaselinesSource), JsonSerializer.Serialize(merged, json) + "\n");
        }
    }

    private static async Task<AgreementResult> ScoreAsync(MigratedDatabase migrated, string key, string tsql)
    {
        var model = Models.GetOrAdd(migrated.Entry.Database, _ => new Lazy<SemanticModel>(() => SemanticModel.Build(migrated.Read.Database))).Value;
        var translation = ScriptTranslator.Translate(tsql, model, CorpusMigration.DefaultOptions, key);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: migrated.SourceConnection);
        if (translation.Diagnostics.FirstOrDefault(d => d.Severity == Severity.Error) is { } error)
            return new AgreementResult(key, 0, expected.Rows.Count, 0, $"not translated: {error.Code} {error.Message}");

        QueryResult actual;
        try
        {
            actual = await BothEngines.RunPostgresAsync(translation.Body, translation.Prelude, migrated.TargetConnection);
        }
        catch (Npgsql.PostgresException ex)
        {
            return new AgreementResult(key, 0, expected.Rows.Count, 0, $"PostgreSQL {ex.SqlState}: {ex.MessageText}");
        }

        if (!actual.Columns.Select(c => c.ToLowerInvariant()).SequenceEqual(expected.Columns.Select(c => c.ToLowerInvariant())))
            return new AgreementResult(key, 0, expected.Rows.Count, actual.Rows.Count,
                $"columns differ: [{string.Join(", ", actual.Columns)}] vs [{string.Join(", ", expected.Columns)}]");

        int total = Math.Max(expected.Rows.Count, actual.Rows.Count);
        if (total == 0)
            return new AgreementResult(key, 100, 0, 0, null);
        var left = expected.Rows.Select(Row).ToList();
        var right = actual.Rows.Select(Row).ToList();
        int matching = BothEngines.HasTopLevelOrderBy(tsql)
            ? left.Zip(right).Count(p => p.First == p.Second)
            : MultisetOverlap(left, right);
        double score = Math.Round(100.0 * matching / total, 1);
        string? problem = null;
        if (matching < total)
        {
            var mismatch = left.Except(right).FirstOrDefault() ?? left.Zip(right).FirstOrDefault(p => p.First != p.Second).First;
            var counterpart = right.Except(left).FirstOrDefault();
            problem = $"{total - matching} row(s) differ, e.g. SQL Server [{Show(mismatch)}] PostgreSQL [{Show(counterpart)}]";
        }
        return new AgreementResult(key, score, expected.Rows.Count, actual.Rows.Count, problem);
    }

    private static string Row(IReadOnlyList<string> row) => string.Join('\u001f', row);

    private static string Show(string? row) => row?.Replace('\u001f', '|') ?? "-";

    private static int MultisetOverlap(List<string> left, List<string> right)
    {
        var counts = right.GroupBy(r => r, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int matching = 0;
        foreach (string row in left)
        {
            if (counts.TryGetValue(row, out int n) && n > 0)
            {
                counts[row] = n - 1;
                matching++;
            }
        }
        return matching;
    }
}
