using Majorsilence.Sql2Postgre.Compat;
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
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// The shop fixture migrated with <see cref="CaseInsensitiveStrategy.IcuCollation"/>: its
/// case-insensitive name column gets the nondeterministic ICU collation. Matches on it agree with
/// SQL Server's case-insensitive collation on every target version: PostgreSQL before 18 allows
/// no LIKE or substring search on such a column, and no version allows regular expressions, so
/// those become <c>lower(name COLLATE "default") …</c> or a case-insensitive regular expression
/// on the default collation.
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
        // The rest, and the mssql_compat helpers the queries call (CHARINDEX, ISNUMERIC).
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS mssql_compat;\n"
            + string.Concat(CompatScripts.CoreNames.Select(n => CompatScripts.Create(n, "mssql_compat") + "\n")), conn).ExecuteNonQueryAsync();
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
    [TestCase("SELECT name, CHARINDEX(N'B', name) AS b, CHARINDEX(N'o', name, 3) AS o FROM shop.customer")]
    [TestCase("SELECT name, CHARINDEX(UPPER(LEFT(name, 1)), name) AS first FROM shop.customer")]
    [TestCase("SELECT name, REPLACE(name, N'B', N'*') AS r, REPLACE(name, N'o', N'\\1') AS backslash, REPLACE(name, N'', N'x') AS empty FROM shop.customer")]
    [TestCase("SELECT c.name, s.value FROM shop.customer c CROSS APPLY STRING_SPLIT(c.name, N'B') s")]
    [TestCase("SELECT name, ISNUMERIC(name) AS n, ISNUMERIC(CustomerCode) AS code FROM shop.customer")]
    public Task Matches(string tsql) =>
        BothEngines.AssertSameResultAsync(tsql, Translate(tsql), null, null, _source.ConnectionString, _target.ConnectionString);
}
