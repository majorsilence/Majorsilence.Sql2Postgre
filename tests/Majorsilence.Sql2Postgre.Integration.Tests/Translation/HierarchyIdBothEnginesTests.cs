using Majorsilence.Sql2Postgre.Compat;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// hierarchyid on both engines: a grid of paths (the root, dotted and negative labels, deep and
/// wide levels, NULL) migrated as text with a GetLevel() computed column, then every method, the
/// depth-first order and comparisons, compared row for row.
/// </summary>
[Category("Containers")]
public class HierarchyIdBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Node (Id int NOT NULL PRIMARY KEY, Node hierarchyid NULL, Lvl AS Node.GetLevel());
        GO
        INSERT INTO dbo.Node (Id, Node) VALUES
            (1, '/'), (2, '/1/'), (3, '/1/1/'), (4, '/1/2/'), (5, '/1/10/'), (6, '/2/'), (7, '/1/1.1/'), (8, '/-1/'),
            (9, '/1/1/3/'), (10, '/10/'), (11, '/1.1/'), (12, '/1/2.5.7/'), (13, NULL), (14, '/1/-2.1/'), (15, '/1/1/3/4/');
        GO
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "hierarchyid");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("hierarchyid");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        var before = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That(before.Failed, Is.Zero, string.Join("\n", before.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        // The queries below use helpers the schema does not.
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS mssql_compat;\n"
            + string.Concat(CompatScripts.Names.Select(n => CompatScripts.Create(n, "mssql_compat") + "\n")), conn).ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [TestCase("SELECT Id, Node.ToString() AS path, Node.GetLevel() AS level, Lvl FROM dbo.Node ORDER BY Node, Id")]
    [TestCase("SELECT Id FROM dbo.Node ORDER BY Node DESC, Id")]
    [TestCase("SELECT Id, Node.GetAncestor(0).ToString() AS a0, Node.GetAncestor(1).ToString() AS a1, Node.GetAncestor(2).ToString() AS a2, Node.GetAncestor(5).ToString() AS a5 FROM dbo.Node ORDER BY Id")]
    [TestCase("SELECT a.Id AS child, b.Id AS parent FROM dbo.Node AS a JOIN dbo.Node AS b ON a.Node.IsDescendantOf(b.Node) = 1 ORDER BY a.Id, b.Id")]
    [TestCase("SELECT a.Id AS lower, b.Id AS higher FROM dbo.Node AS a JOIN dbo.Node AS b ON a.Node < b.Node ORDER BY a.Id, b.Id")]
    [TestCase("SELECT Id, Node.GetReparentedValue(CAST('/1/' AS hierarchyid), CAST('/2/5/' AS hierarchyid)).ToString() AS moved FROM dbo.Node WHERE Node.IsDescendantOf(CAST('/1/' AS hierarchyid)) = 1 ORDER BY Id")]
    [TestCase("SELECT hierarchyid::GetRoot().ToString() AS root, hierarchyid::Parse('/3/4/').GetLevel() AS level")]
    [TestCase("SELECT e.Id, p.Id AS manager FROM dbo.Node AS e JOIN dbo.Node AS p ON p.Node = e.Node.GetAncestor(1) ORDER BY e.Id")]
    public async Task SameRows(string tsql) => await AssertSameAsync(tsql);

    [TestCase(null, null)]
    [TestCase("/1/3/", null)]
    [TestCase(null, "/1/3/")]
    [TestCase("/1/1/", "/1/3/")]
    [TestCase("/1/1/", "/1/2/")]
    [TestCase("/1/1.1/", "/1/2/")]
    [TestCase("/1/1/", "/1/1.1/")]
    [TestCase("/1/1.5/", "/1/2/")]
    [TestCase("/1/-1/", "/1/1/")]
    [TestCase("/1/1.1/", "/1/1.2/")]
    [TestCase("/1/1.1/", "/1/1.3/")]
    [TestCase("/1/1.1.1/", "/1/1.2/")]
    [TestCase("/1/-3/", "/1/-2/")]
    [TestCase(null, "/1/-2/")]
    [TestCase("/1/1.9/", null)]
    public async Task GetDescendant(string? child1, string? child2)
    {
        string Arg(string? value) => value is null ? "NULL" : $"CAST('{value}' AS hierarchyid)";
        await AssertSameAsync($"SELECT CAST('/1/' AS hierarchyid).GetDescendant({Arg(child1)}, {Arg(child2)}).ToString() AS child");
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
