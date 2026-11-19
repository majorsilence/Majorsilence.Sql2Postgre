using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// char(n) semantics on both engines. SQL Server keeps a char value's trailing spaces wherever it
/// becomes a string (CASE, COALESCE, +, CAST to varchar, string functions), ignores them in
/// comparisons, and lets non-Unicode LIKE leave them off; PostgreSQL drops them when bpchar becomes
/// text. Results are wrapped in [ ] so the padding shows.
/// </summary>
[Category("Containers")]
public class CharBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, C char(6) NOT NULL, N nchar(5) NOT NULL, V varchar(10) NOT NULL, CN char(6) NULL, S char(3) NULL);
        GO
        INSERT INTO dbo.T VALUES (1, 'ab', N'xy', 'ab', NULL, 'abc'), (2, '', N'', '', 'q', NULL), (3, 'a b', N'a b', 'a b', 'z z', 'x');
        GO
        CREATE PROCEDURE dbo.Report @type char(6) AS
            SELECT CASE WHEN GROUPING(C) = 1 THEN 'ALL' ELSE C END AS C, COUNT(*) AS n
            FROM dbo.T WHERE C = @type OR C LIKE '%b%' GROUP BY C WITH ROLLUP;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "chars");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("chars");
        var schema = SchemaConverter.Convert(db, Options);
        var before = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That(before.Failed, Is.Zero);
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    // CASE, IIF, COALESCE, ISNULL
    [TestCase("'[' + CASE WHEN Id = 1 THEN C ELSE 'x' END + ']'")]
    [TestCase("'[' + CASE WHEN Id = 1 THEN 'x' ELSE C END + ']'")]
    [TestCase("'[' + CASE WHEN Id = 1 THEN N ELSE V END + ']'")]
    [TestCase("'[' + CASE WHEN Id = 1 THEN S ELSE C END + ']'")]
    [TestCase("'[' + IIF(Id = 1, C, 'x') + ']'")]
    [TestCase("'[' + ISNULL(CN, C) + ']'")]
    [TestCase("'[' + ISNULL(C, 'zz') + ']'")]
    [TestCase("'[' + COALESCE(CN, C, 'q') + ']'")]
    [TestCase("'[' + COALESCE(V, C) + ']'")]
    [TestCase("'[' + CASE WHEN Id = 2 THEN NULL ELSE C END + ']'")]
    [TestCase("CASE WHEN Id = 1 THEN C ELSE 'x' END")]
    // +
    [TestCase("'[' + C + 'x' + ']'")]
    [TestCase("'[' + C + C + ']'")]
    [TestCase("'[' + V + C + ']'")]
    [TestCase("'[' + N + N'!' + ']'")]
    // CAST and CONVERT
    [TestCase("'[' + CAST(C AS varchar(10)) + ']'")]
    [TestCase("'[' + CAST(C AS varchar(3)) + ']'")]
    [TestCase("'[' + CONVERT(varchar, C) + ']'")]
    [TestCase("'[' + CAST(C AS nvarchar(10)) + ']'")]
    [TestCase("'[' + CAST(C AS char(8)) + ']'")]
    // string functions
    [TestCase("'[' + UPPER(C) + ']'")]
    [TestCase("'[' + LEFT(C, 4) + ']'")]
    [TestCase("'[' + RIGHT(C, 3) + ']'")]
    [TestCase("'[' + SUBSTRING(C, 1, 4) + ']'")]
    [TestCase("'[' + REPLACE(C, 'a', 'z') + ']'")]
    [TestCase("'[' + LTRIM(C) + ']'")]
    [TestCase("'[' + REVERSE(C) + ']'")]
    [TestCase("'[' + CONCAT(C, 'x') + ']'")]
    [TestCase("CHARINDEX(' ', C)")]
    [TestCase("LEN(C)")]
    [TestCase("LEN(C + 'x')")]
    [TestCase("DATALENGTH(C)")]
    [TestCase("C")]
    [TestCase("N")]
    // comparisons ignore trailing spaces
    [TestCase("CASE WHEN C = 'ab' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN C = V THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN C = 'ab   ' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN CASE WHEN Id = 1 THEN C ELSE 'x' END = 'ab' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN C + '' = 'ab' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN ISNULL(CN, C) IN ('ab', 'q') THEN 1 ELSE 0 END")]
    [TestCase("CASE CAST(C AS varchar(10)) WHEN 'ab' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN UPPER(N) = 'XY' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN LEFT(C, 4) = 'ab' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN RIGHT(C, 2) = '' THEN 1 ELSE 0 END")]
    public async Task SameValues(string expression) =>
        await AssertSameAsync($"SELECT Id, {expression} AS v FROM dbo.T ORDER BY Id");

    [TestCase("C LIKE 'ab'")]
    [TestCase("C LIKE 'ab%'")]
    [TestCase("C LIKE '%b'")]
    [TestCase("C LIKE 'ab    '")]
    [TestCase("C LIKE ''")]
    [TestCase("C LIKE '_'")]
    [TestCase("C LIKE '__'")]
    [TestCase("C LIKE '___'")]
    [TestCase("C LIKE '______'")]
    [TestCase("C LIKE '_______'")]
    [TestCase("C LIKE 'ab_'")]
    [TestCase("C LIKE 'ab %'")]
    [TestCase("C LIKE '% '")]
    [TestCase("C LIKE 'a%b'")]
    [TestCase("C NOT LIKE 'ab_'")]
    [TestCase("C LIKE N'ab'")]
    [TestCase("N LIKE 'xy'")]
    [TestCase("N LIKE 'xy%'")]
    [TestCase("N LIKE '%y'")]
    [TestCase("N LIKE '_____'")]
    [TestCase("'ab' LIKE C")]
    public async Task SameLike(string predicate) =>
        await AssertSameAsync($"SELECT Id, CASE WHEN {predicate} THEN 1 ELSE 0 END AS m FROM dbo.T ORDER BY Id");

    [TestCase("SELECT STRING_AGG('[' + C + ']', ',') WITHIN GROUP (ORDER BY Id) AS v FROM dbo.T")]
    [TestCase("SELECT STRING_AGG(C, ',') WITHIN GROUP (ORDER BY Id) AS v FROM dbo.T")]
    [TestCase("SELECT '[' + MAX(C) + ']' AS v FROM dbo.T")]
    [TestCase("SELECT Id FROM dbo.T WHERE C LIKE '_b' ORDER BY Id")]
    public async Task SameResult(string tsql) => await AssertSameAsync(tsql);

    /// <summary>pubs' reptq3 shape: a ROLLUP whose CASE puts 'ALL' beside a char column (rows in any order).</summary>
    [Test]
    public async Task RollupLabelsKeepThePadding()
    {
        const string tsql = "EXEC dbo.Report 'ab'";
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        await BothEngines.AssertSameResultAsync(tsql, translation.Sql, null, null, _source.ConnectionString, _target.ConnectionString);
    }

    private async Task AssertSameAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }
}
