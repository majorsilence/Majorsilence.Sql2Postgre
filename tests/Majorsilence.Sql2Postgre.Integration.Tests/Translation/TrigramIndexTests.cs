using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// pg_trgm indexes on PostgreSQL: the converter creates one for a column a converted view searches
/// with LIKE '%…%', and PostgreSQL uses it for that search (EXPLAIN).
/// </summary>
[Category("Containers")]
public class TrigramIndexTests
{
    private const string Script = """
        CREATE TABLE dbo.Customer (CustomerId int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
        GO
        CREATE VIEW dbo.Smiths AS SELECT CustomerId, Name FROM dbo.Customer WHERE Name LIKE N'%smith%';
        GO
        """;

    [Test]
    public async Task TheIndexIsCreatedAndUsed()
    {
        var db = SqlServerScriptReader.Read([("trgm.sql", Script)]).Database;
        var options = new ConversionOptions { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string> { PermittedExtensions.PgTrgm } };
        var schema = SchemaConverter.Convert(db, options);
        Assert.That(schema.Objects.Where(o => o.Kind == ObjectKind.Index).Select(o => o.TargetName), Does.Contain("public.trgm_customer_name"));

        await using var target = await PostgresDatabase.CreateAsync("trgm");
        var applied = await PgApplier.ApplyAsync(schema.Objects, target.ConnectionString);
        Assert.That(applied.Failed, Is.Zero, string.Join("\n", applied.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));

        await using var conn = new NpgsqlConnection(target.ConnectionString);
        await conn.OpenAsync();
        await using (var fill = new NpgsqlCommand("""
            INSERT INTO public.customer SELECT g, 'customer ' || g || CASE WHEN g % 500 = 0 THEN ' smith' ELSE '' END FROM generate_series(1, 20000) AS g;
            ANALYZE public.customer;
            """, conn))
            await fill.ExecuteNonQueryAsync();
        var translation = ScriptTranslator.Translate("SELECT CustomerId FROM dbo.Customer WHERE Name LIKE N'%smith%'", SemanticModel.Build(db), options);
        // With 20,000 short rows the planner may prefer a sequential scan; whether it can use the index is the question.
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", conn))
            await off.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + translation.Sql.TrimEnd().TrimEnd(';'), conn);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        Assert.That(string.Join("\n", plan), Does.Contain("trgm_customer_name"), translation.Sql);
    }
}
