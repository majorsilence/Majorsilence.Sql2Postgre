using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Types;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

[Category("Containers")]
public class TypeMapperIntegrationTests
{
    /// <summary>
    /// Every column type of the shop fixture's all_types table, mapped with no extensions enabled
    /// (the test PostgreSQL has none), creates on the PostgreSQL under test.
    /// </summary>
    [Test]
    public async Task EveryMappedTypeCreates()
    {
        string script = SqlServerDatabase.Fixture("shop.sql");
        var table = SqlServerScriptReader.Read([("shop.sql", script)]).Database.Tables.Single(t => t.Name.Name == "all_types");
        var options = new ConversionOptions { Extensions = new HashSet<string>() };
        var names = new NameMapper(options);
        var mapper = new TypeMapper(options, names);

        var columns = table.Columns
            .Where(c => c.Type.UserTypeName is null)
            .Select(c => $"{NameMapper.Quote(names.Column(table.Name, c.Name))} {mapper.Map(c.Type, table.Name, c.Name).PgType}")
            .ToList();
        string ddl = $"CREATE TABLE all_types ({string.Join(", ", columns)});";

        var result = await BothEngines.RunPostgresAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'all_types'", ddl);
        Assert.That(result.Rows.Single()[0], Is.EqualTo(columns.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
