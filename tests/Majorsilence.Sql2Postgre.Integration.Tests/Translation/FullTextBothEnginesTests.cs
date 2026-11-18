using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Full-text search on both engines. The table is read from a SQL Server with Full-Text Search
/// installed, its full-text index becomes a GIN index, and the same CONTAINS, FREETEXT,
/// CONTAINSTABLE and FREETEXTTABLE searches return the same rows, in the same order where they
/// order by rank. The conditions avoid the documented differences (S2P3042): stemming where
/// SQL Server matches only the word itself, stop words, and hyphenated words.
/// </summary>
[Category("Containers")]
public class FullTextBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Doc (Id int NOT NULL CONSTRAINT PK_Doc PRIMARY KEY, Title nvarchar(200) NULL, Body nvarchar(max) NULL, Notes xml NULL);
        GO
        INSERT INTO dbo.Doc VALUES
            (1, N'Red apple', N'The quick brown fox jumps over the lazy dog', NULL),
            (2, N'Green pear', N'Running runners ran quickly to the mountain bike race', N'<r><b>mountain</b> lake</r>'),
            (3, N'Apple pie', N'Baking an apple pie with cinnamon and sugar', NULL),
            (4, N'Blue', N'apple banana', N'<r>banana split</r>'),
            (5, NULL, N'mountainous terrain and bikes', NULL),
            (6, N'fox', N'brown dogs are lazy; the fox is quick', NULL),
            (7, N'Cherry', N'cherry cherry cherry', NULL),
            (8, N'Orchard', N'the orchard grows plums, pears, apricots, figs, quinces and one cherry tree near the old stone wall by the river', NULL);
        GO
        CREATE FULLTEXT CATALOG DocCatalog AS DEFAULT;
        GO
        CREATE FULLTEXT INDEX ON dbo.Doc (Title LANGUAGE 1033, Body LANGUAGE 1033, Notes LANGUAGE 1033) KEY INDEX PK_Doc WITH CHANGE_TRACKING AUTO;
        GO
        CREATE PROCEDURE dbo.Search @q nvarchar(400), @lang int = 0, @inflect bit = 0 AS
        BEGIN
            IF @inflect = 1
                SET @q = 'FORMSOF(INFLECTIONAL, "' + @q + '")';
            IF @lang = 0
                SELECT @lang = CONVERT(int, SERVERPROPERTY('LCID'));
            SELECT d.Id FROM dbo.Doc AS d JOIN CONTAINSTABLE(dbo.Doc, *, @q, LANGUAGE @lang) AS k ON d.Id = k.[KEY] ORDER BY d.Id;
        END;
        GO
        CREATE PROCEDURE dbo.Similar @words nvarchar(400) AS
            SELECT Id FROM dbo.Doc WHERE FREETEXT(Body, @words) ORDER BY Id;
        GO
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "fulltext", await Engines.SqlServerFullTextAsync());
        await PopulatedAsync(_source.ConnectionString, "dbo.Doc", 8);
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        var index = db.Tables.Single().Indexes.Single(i => i.Type == "fulltext");
        Assert.That(index.FullText, Is.EqualTo(new FullTextIndex("Id", [new FullTextColumn("Title", 1033), new FullTextColumn("Body", 1033), new FullTextColumn("Notes", 1033)]))
            .Using<FullTextIndex>((a, b) => a.KeyColumn == b.KeyColumn && a.StopList == b.StopList && a.Columns.SequenceEqual(b.Columns)));
        _target = await PostgresDatabase.CreateAsync("fulltext");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        var before = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That(before.Failed, Is.Zero, string.Join("\n", before.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
    }

    /// <summary>Waits for SQL Server to finish indexing the table (population is asynchronous).</summary>
    private static async Task PopulatedAsync(string connectionString, string table, int rows)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        string sql = $"""
            SELECT CASE WHEN OBJECTPROPERTYEX(OBJECT_ID('{table}'), 'TableFulltextPopulateStatus') = 0
                AND OBJECTPROPERTYEX(OBJECT_ID('{table}'), 'TableFulltextPendingChanges') = 0
                AND OBJECTPROPERTYEX(OBJECT_ID('{table}'), 'TableFulltextItemCount') = {rows} THEN 1 ELSE 0 END
            """;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            await using var cmd = new SqlCommand(sql, conn);
            if ((int)(await cmd.ExecuteScalarAsync())! == 1)
                return;
            await Task.Delay(500);
        }
        Assert.Fail($"The full-text index on {table} was not populated within a minute.");
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [TestCase("CONTAINS(*, 'apple')")]
    [TestCase("CONTAINS(*, '\"apple\" AND \"pie\"')")]
    [TestCase("CONTAINS(*, '\"red\" AND \"fox\"')")]
    [TestCase("CONTAINS((Title, Body), '\"apple\" AND \"banana\"')")]
    [TestCase("CONTAINS(Body, '\"moun*\"')")]
    [TestCase("CONTAINS(Body, '\"quick brown\"')")]
    [TestCase("CONTAINS(Body, '\"brown quick\"')")]
    [TestCase("CONTAINS(Body, '\"quick* brown\"')")]
    [TestCase("CONTAINS(Body, 'NEAR((fox, lazy), 2)')")]
    [TestCase("CONTAINS(Body, 'NEAR((fox, lazy), 3, TRUE)')")]
    [TestCase("CONTAINS(Body, 'NEAR((fox, lazy), 3)')")]
    [TestCase("CONTAINS(Body, '\"fox\" NEAR \"lazy\"')")]
    [TestCase("CONTAINS(Body, '\"apple\" AND NOT \"pie\"')")]
    [TestCase("CONTAINS(Body, '\"apple\" &! \"pie\"')")]
    [TestCase("CONTAINS(Body, '\"apple\" OR \"fox\"')")]
    [TestCase("CONTAINS(Body, '\"apple\" | \"fox\" & \"lazy\"')")]
    [TestCase("CONTAINS(Body, '\"apple\" AND (\"pie\" OR \"banana\")')")]
    [TestCase("CONTAINS(Body, 'APPLE')")]
    [TestCase("CONTAINS(Body, 'FORMSOF(INFLECTIONAL, run)')")]
    [TestCase("CONTAINS(Body, 'FORMSOF(INFLECTIONAL, bike, banana)')")]
    [TestCase("CONTAINS(Body, 'ISABOUT (apple WEIGHT (.8), fox WEIGHT (.2))')")]
    [TestCase("CONTAINS(Body, '\"the fox\"')")]
    [TestCase("CONTAINS(Notes, 'mountain')")]
    [TestCase("CONTAINS(Body, 'cherry', LANGUAGE 1033)")]
    [TestCase("CONTAINS(Body, 'cherry', LANGUAGE 'English')")]
    [TestCase("FREETEXT(Body, 'running bikes mountain')")]
    [TestCase("FREETEXT(*, 'red apples')")]
    [TestCase("FREETEXT(Title, 'pears')")]
    [TestCase("NOT CONTAINS(Body, 'apple') AND Id < 7")]
    public async Task SameRows(string predicate) =>
        await AssertSameAsync($"SELECT Id FROM dbo.Doc WHERE {predicate} ORDER BY Id");

    [TestCase("SELECT d.Id, d.Title FROM dbo.Doc AS d JOIN CONTAINSTABLE(dbo.Doc, *, 'apple') AS k ON d.Id = k.[KEY] ORDER BY d.Id")]
    [TestCase("SELECT k.[KEY] FROM CONTAINSTABLE(dbo.Doc, Body, '\"apple\" OR \"fox\"') AS k ORDER BY k.[KEY]")]
    [TestCase("SELECT k.[KEY] FROM CONTAINSTABLE(dbo.Doc, Body, 'cherry') AS k ORDER BY k.RANK DESC")]
    [TestCase("SELECT k.[KEY] FROM CONTAINSTABLE(dbo.Doc, Body, 'cherry', 1) AS k")]
    [TestCase("SELECT k.[KEY] FROM FREETEXTTABLE(dbo.Doc, (Title, Body), 'pie cherry') AS k ORDER BY k.[KEY]")]
    [TestCase("SELECT d.Title FROM FREETEXTTABLE(dbo.Doc, Body, 'cherry', LANGUAGE 1033) AS k JOIN dbo.Doc AS d ON d.Id = k.[KEY] ORDER BY k.RANK DESC")]
    [TestCase("SELECT COUNT(*) AS n FROM dbo.Doc AS d WHERE EXISTS (SELECT 1 FROM CONTAINSTABLE(dbo.Doc, Title, 'apple') AS k WHERE k.[KEY] = d.Id)")]
    [TestCase("SELECT CONVERT(int, SERVERPROPERTY('LCID')) AS lcid")]
    [TestCase("EXEC dbo.Search N'\"apple\" AND \"pie\"'")]
    [TestCase("EXEC dbo.Search N'\"moun*\" OR fox', 1033")]
    [TestCase("EXEC dbo.Search N'run', 0, 1")]
    [TestCase("EXEC dbo.Similar N'running bikes'")]
    public async Task SameResult(string tsql) => await AssertSameAsync(tsql);

    /// <summary>PostgreSQL searches the converted index: the GIN index answers a CONTAINS on one column.</summary>
    [Test]
    public async Task UsesTheIndex()
    {
        var translation = ScriptTranslator.Translate("SELECT Id FROM dbo.Doc WHERE CONTAINS(Body, 'apple')", _model, Options);
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", conn))
            await off.ExecuteNonQueryAsync();
        // The translation creates the helpers it uses first; the query is its last statement.
        string query = translation.Sql[translation.Sql.LastIndexOf("SELECT", StringComparison.Ordinal)..].TrimEnd().TrimEnd(';');
        await using var cmd = new NpgsqlCommand("EXPLAIN " + query, conn);
        var plan = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        Assert.That(string.Join("\n", plan), Does.Contain("Bitmap Index Scan on fulltext_doc"));
    }

    /// <summary>The converter's language table (constant LANGUAGE, index columns) and the run-time helper's agree.</summary>
    [Test]
    public async Task LanguageTablesAgree()
    {
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        foreach (var (lcid, config) in FullTextSearch.Mapped.Append(new KeyValuePair<int, string>(1041, "simple")))
        {
            await using var cmd = new NpgsqlCommand("SELECT mssql_compat.fulltext_config($1)::text", conn) { Parameters = { new() { Value = lcid } } };
            Assert.That(await cmd.ExecuteScalarAsync(), Is.EqualTo(config), $"LCID {lcid}");
        }
    }

    /// <summary>A condition SQL Server rejects is rejected on PostgreSQL too.</summary>
    [TestCase("NOT \"apple\"")]
    [TestCase("apple pie")]
    [TestCase("\"apple\" OR NOT \"pie\"")]
    public async Task SyntaxErrors(string condition)
    {
        string tsql = $"SELECT Id FROM dbo.Doc WHERE CONTAINS(Body, '{condition}')";
        await Assert.ThrowsAsync<SqlException>(() => BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString));
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString));
        Assert.That(ex!.SqlState, Is.EqualTo("42601"));
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
