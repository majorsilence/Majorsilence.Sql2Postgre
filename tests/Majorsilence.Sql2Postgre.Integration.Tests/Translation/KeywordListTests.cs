using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Translation.Names;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

[Category("Containers")]
public class KeywordListTests
{
    /// <summary>
    /// Every keyword the running PostgreSQL treats as other than unreserved must be quoted, so it
    /// must be in the embedded list (taken from PostgreSQL 18; older versions have fewer).
    /// </summary>
    [Test]
    public async Task EmbeddedListCoversTheServersKeywords()
    {
        var result = await BothEngines.RunPostgresAsync("SELECT word FROM pg_get_keywords() WHERE catcode <> 'U' ORDER BY word");
        var serverKeywords = result.Rows.Select(r => r[0]).ToList();
        Assert.That(serverKeywords, Is.Not.Empty);
        Assert.That(serverKeywords.Where(k => !NameMapper.NonUnreservedKeywords.Contains(k)), Is.Empty);
    }

    [Test]
    public async Task QuotedNamesCreateAndPlainNamesResolve()
    {
        // A table whose name and columns are all keywords or need quoting creates, and a plain
        // lower-case name reads back unquoted as the same identifier.
        string table = NameMapper.Quote("order");
        string setup = $"CREATE TABLE {table} ({NameMapper.Quote("user")} integer, {NameMapper.Quote("OrderID")} integer, plain_name integer);"
                       + $" INSERT INTO {table} VALUES (1, 2, 3);";
        var result = await BothEngines.RunPostgresAsync($"SELECT \"user\", \"OrderID\", plain_name FROM {table}", setup);
        Assert.That(result.Rows.Single(), Is.EqualTo(new[] { "1", "2", "3" }));
    }
}
