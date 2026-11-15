using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Postgres;

[Category("Containers")]
public class ScriptTranslatorIntegrationTests
{
    /// <summary>A translated script runs as it is on PostgreSQL, helpers and all, and gives SQL Server's answers.</summary>
    [Test]
    public async Task TranslatedScriptRunsOnItsOwn()
    {
        const string script = """
            SET NOCOUNT ON;
            GO
            SELECT DATEDIFF(month, '2024-01-31', '2024-03-01') AS months, ISNUMERIC('1e5') AS n, CHARINDEX('b', 'abcb', 3) AS pos;
            """;
        var translation = ScriptTranslator.Translate(script, SemanticModel.Build(new SourceDatabase("", "", 160, [], [], [], [], [], [])), new ConversionOptions());
        var expected = await BothEngines.RunSqlServerAsync("SELECT DATEDIFF(month, '2024-01-31', '2024-03-01') AS months, ISNUMERIC('1e5') AS n, CHARINDEX('b', 'abcb', 3) AS pos");

        await using var db = await PostgresDatabase.CreateAsync("script");
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var batch = new NpgsqlCommand(translation.Sql, conn);
        await using var reader = await batch.ExecuteReaderAsync();
        var rows = new List<string[]>();
        do
        {
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Majorsilence.Sql2Postgre.Data.CanonicalValue.Format(reader.GetValue(i), reader.GetDataTypeName(i))).ToArray());
        }
        while (await reader.NextResultAsync());
        Assert.That(rows.Last(), Is.EqualTo(expected.Rows.Single()));
    }
}
