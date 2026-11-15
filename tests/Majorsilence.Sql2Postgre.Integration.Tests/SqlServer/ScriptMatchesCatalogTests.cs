using System.Text.Json;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Integration.Tests.SqlServer;

/// <summary>
/// The script reader and the catalog reader must agree on everything a script can express. The
/// fixture is created on SQL Server and read back from its catalog, and the same text is read
/// by the script reader; the two models are compared after removing what only one side can know.
/// </summary>
[Category("Containers")]
public partial class ScriptMatchesCatalogTests
{
    [Test]
    public async Task ShopFixtureReadsTheSameBothWays()
    {
        string script = SqlServerDatabase.Fixture("shop.sql");
        await using var db = await SqlServerDatabase.CreateAsync(script, "shop");
        var fromCatalog = (await SqlServerCatalogReader.ReadAsync(db.ConnectionString)).Database;
        var fromScript = SqlServerScriptReader.Read([("shop.sql", script)]).Database;

        string Json(SourceDatabase d) => JsonSerializer.Serialize(Comparable(d), new JsonSerializerOptions { WriteIndented = true });
        string expected = Json(fromCatalog), actual = Json(fromScript);
        if (actual != expected)
        {
            // Both sides on disk, for a diff tool: the assertion message shows only the first difference.
            string dir = TestContext.CurrentContext.WorkDirectory;
            File.WriteAllText(Path.Combine(dir, "shop.catalog.json"), expected);
            File.WriteAllText(Path.Combine(dir, "shop.script.json"), actual);
        }
        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <summary>
    /// What only the catalog knows is blanked on both sides: the database's name, collation and
    /// level, row counts, dependencies, a sequence's current value, a computed column's type and
    /// nullability (SQL Server derives them from the expression), a column's collation where the
    /// script did not name one (it is the database default), and the text of a module created
    /// WITH ENCRYPTION. Expressions are compared without SQL Server's normalization (it adds
    /// parentheses and brackets and lower-cases keywords).
    /// </summary>
    private static object Comparable(SourceDatabase d) => new
    {
        // (Column collations equal to the database default are what SQL Server filled in.)
        d.Schemas,
        Tables = d.Tables.Select(t => new
        {
            t.Name,
            Columns = t.Columns.Select(c => Column(c, d.DefaultCollation)).ToList(),
            t.PrimaryKey, t.Uniques, t.ForeignKeys,
            Checks = t.Checks.Select(c => c with { Expression = Expr(c.Expression) }).ToList(),
            Indexes = t.Indexes.Select(i => new { i.Name, i.Keys, i.Included, i.IsUnique, i.IsClustered, Filter = i.FilterExpression is null ? null : Expr(i.FilterExpression), i.Type }).ToList(),
        }).ToList(),
        Sequences = d.Sequences.Select(s => s with { CurrentValue = null }).ToList(),
        UserTypes = d.UserTypes.Select(u => new
        {
            u.Name, u.IsNullable, u.IsTableType,
            BaseType = u.IsTableType ? null : u.BaseType with { Collation = null },
            TableColumns = u.TableColumns.Select(c => Column(c, d.DefaultCollation)).ToList(),
        }).ToList(),
        Modules = d.Modules.Select(m => m with
        {
            Definition = m.Name.Name == "secret_proc" ? "" : m.Definition.TrimEnd().TrimEnd(';').TrimEnd(),
        }).ToList(),
    };

    private static object Column(Column c, string databaseCollation) => new
    {
        c.Name, c.Ordinal,
        Type = c.Computed is not null ? null : c.Type with { Collation = c.Type.Collation == databaseCollation ? null : c.Type.Collation },
        IsNullable = c.Computed is not null ? (bool?)null : c.IsNullable,
        c.Identity,
        Default = c.DefaultExpression is null ? null : Expr(c.DefaultExpression),
        c.DefaultConstraintName,
        Computed = c.Computed is null ? null : new { Expression = Expr(c.Computed.Expression), c.Computed.IsPersisted },
        c.IsRowGuidCol, c.IsSparse,
    };

    private static string Expr(string expression) =>
        Noise().Replace(expression, "").ToLowerInvariant();

    [GeneratedRegex(@"[\s()\[\]]")]
    private static partial Regex Noise();
}
