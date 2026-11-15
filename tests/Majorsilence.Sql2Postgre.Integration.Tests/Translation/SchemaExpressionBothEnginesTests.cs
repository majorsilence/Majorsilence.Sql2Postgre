using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.Translation.Expressions;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Types;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// What each translated function and operator returns on PostgreSQL equals what the T-SQL
/// returns on SQL Server, for fixed inputs. Each case is <c>SELECT &lt;expr&gt; AS v</c> on both.
/// </summary>
[Category("Containers")]
public class SchemaExpressionBothEnginesTests
{
    [TestCase("ROUND(2.5, 0)")]
    [TestCase("ROUND(-2.5, 0)")]
    [TestCase("ROUND(1.005, 2)")]
    [TestCase("ROUND(CAST(2.25 AS float), 1)")]
    [TestCase("LTRIM('  a ')")]
    [TestCase("RTRIM(' a  ')")]
    [TestCase("UPPER(N'abc')")]
    [TestCase("LOWER('ABC')")]
    [TestCase("ABS(-3)")]
    [TestCase("ABS(-3.25)")]
    [TestCase("ISNULL(NULL, 1)")]
    [TestCase("ISNULL('x', 'y')")]
    [TestCase("COALESCE(NULL, NULL, 2)")]
    [TestCase("NULLIF(1, 1)")]
    [TestCase("NULLIF(1, 2)")]
    [TestCase("IIF(1 > 0, 'y', 'n')")]
    [TestCase("CASE 2 WHEN 1 THEN 'one' WHEN 2 THEN 'two' END")]
    [TestCase("N'a' + N'b'")]
    [TestCase("N'a' + NULL")]
    [TestCase("7 % 3")]
    [TestCase("-7 % 3")]
    [TestCase("7 / 2")]
    [TestCase("7.0 / 2")]
    [TestCase("(1 + 2) * 3 - 4")]
    [TestCase("5 & 3")]
    [TestCase("5 | 3")]
    [TestCase("5 ^ 3")]
    [TestCase("CAST(12.5 AS int)")]
    [TestCase("CAST(-12.5 AS int)")]
    [TestCase("CAST(12.99 AS bigint)")]
    [TestCase("CAST(CAST(-7.9 AS float) AS smallint)")]
    [TestCase("CAST($3.75 AS int)")]
    [TestCase("CAST(42 AS varchar(10))")]
    [TestCase("CAST(1 AS bit)")]
    [TestCase("$12.50")]
    [TestCase("0x00ABFF")]
    public async Task SameValue(string tsql)
    {
        var options = new ConversionOptions();
        var names = new NameMapper(options);
        var result = new SchemaExpressionTranslator(options, names, new TypeMapper(options, names))
            .Translate(tsql, new ObjectName("dbo", "t"), []);
        Assert.That(result.Expression, Is.Not.Null, string.Join("; ", result.Diagnostics));
        await BothEngines.AssertSameResultAsync($"SELECT {tsql} AS v", $"SELECT {PgWriter.Expression(result.Expression!)} AS v");
    }
}
