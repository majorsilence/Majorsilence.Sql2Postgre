using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// The shop fixture migrated with <see cref="CaseInsensitiveStrategy.IcuCollation"/>: its
/// case-insensitive name column gets the nondeterministic ICU collation. Matches on it agree with
/// SQL Server's case-insensitive collation on every target version: PostgreSQL before 18 allows
/// no LIKE on such a column, and no version allows SIMILAR TO, so those become
/// <c>lower(name COLLATE "default") …</c>.
/// </summary>
[Category("Containers")]
public class CaseInsensitiveCollationBothEnginesTests
{
    private const string Data = """
        INSERT INTO shop.customer (CustomerCode, name, email) VALUES ('C1', N'Alice', N'a@x'), ('C2', N'bob', NULL), ('C3', N'Carol', N'c@x'), ('C4', N'dave', N'D@x'), ('C5', N'BOBBY', NULL);
        """;

    private static readonly ConversionOptions Options = new()
    {
        TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>(), CaseInsensitive = CaseInsensitiveStrategy.IcuCollation,
    };

    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql") + "\nGO\n" + Data, "ci_collation");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("ci_collation");
        var schema = SchemaConverter.Convert(db, Options);
        var tables = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That(tables.Failed, Is.Zero);
        var copy = await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString);
        Assert.That(copy.Succeeded, Is.True);
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    private string Translate(string tsql)
    {
        var script = (TSqlScript)new TSql160Parser(true).Parse(new StringReader(tsql), out var errors);
        Assert.That(errors, Is.Empty, $"'{tsql}' does not parse");
        var names = new NameMapper(Options);
        var queries = new QueryTranslator(new ExpressionTranslator(Options, names, new TypeMapper(Options, names), _model));
        return string.Concat(script.Batches.SelectMany(b => b.Statements)
            .Select(s => PgWriter.Write(queries.Statement(s, new ExpressionContext(new Scope(), "dbo", new ObjectName("dbo", "test"))), Options.TargetVersion)));
    }

    [TestCase("SELECT name FROM shop.customer WHERE name = N'ALICE'")]
    [TestCase("SELECT name FROM shop.customer WHERE name LIKE N'b%'")]
    [TestCase("SELECT name FROM shop.customer WHERE name LIKE N'%O%'")]
    [TestCase("SELECT name FROM shop.customer WHERE name NOT LIKE N'_A%'")]
    [TestCase("SELECT name FROM shop.customer WHERE name LIKE N'[a-c]%'")]
    [TestCase("SELECT name FROM shop.customer WHERE name NOT LIKE N'[A-C]%'")]
    [TestCase("SELECT name FROM shop.customer WHERE email LIKE N'd@%'")]
    public Task Matches(string tsql) =>
        BothEngines.AssertSameResultAsync(tsql, Translate(tsql), null, null, _source.ConnectionString, _target.ConnectionString);
}
