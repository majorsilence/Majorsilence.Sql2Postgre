using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Tests.Postgres;

public class CommandTranslatorTests
{
    private static SemanticModel Shop() => SemanticModel.Build(SqlServerScriptReader.Read([("shop.sql",
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")))]).Database);

    private static readonly SemanticModel Empty = SemanticModel.Build(new SourceDatabase("", "", 160, [], [], [], [], [], []));

    private static CommandTranslation T(string sql, SemanticModel? model = null) => CommandTranslator.Translate(sql, model ?? Shop(), new ConversionOptions());

    [Test]
    public void ParametersStayParameters()
    {
        var r = T("SELECT name FROM shop.customer WHERE customer_id = @Id AND name LIKE @name + '%'");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Is.EqualTo("SELECT name\nFROM shop.customer\nWHERE customer_id = @Id AND name LIKE @name || '%'"));
            Assert.That(r.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty);
        });
    }

    [Test]
    public void InsertThenScopeIdentityIsOneInsertReturning()
    {
        var r = T("INSERT INTO shop.[order] (customer_id, order_date, total) VALUES (@c, @d, @t); SELECT CAST(SCOPE_IDENTITY() AS int) AS id");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("INSERT INTO shop.\"order\" (customer_id, order_date, total)"));
            Assert.That(r.Sql, Does.EndWith("RETURNING CAST(trunc(CAST(order_id AS numeric)) AS integer) AS id"));
            Assert.That(r.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void WithoutTheSchemaScopeIdentityReadsLastval()
    {
        var r = T("INSERT INTO dbo.thing (name) VALUES (@name);\nSELECT SCOPE_IDENTITY();", Empty);
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.EndWith(";\nSELECT CAST(lastval() AS numeric)"));
            Assert.That(r.Diagnostics.Select(d => d.Code), Does.Contain(DiagnosticCodes.IdentityFromLastval));
        });
    }

    [Test]
    public void IntCountsAndSumsAreCastToInteger()
    {
        var r = T("SELECT COUNT(*) AS n, COUNT_BIG(*) AS big, SUM(customer_id) AS total, MAX(customer_id) AS highest FROM shop.customer");
        Assert.That(r.Sql, Does.StartWith("SELECT CAST(count(*) AS integer) AS n, count(*) AS big, CAST(sum(customer_id) AS integer) AS total, max(customer_id) AS highest"));
    }

    [Test]
    public void ProcedureCallWithParametersIsACall()
    {
        var r = T("EXEC shop.add_order @customer_id = @c");
        Assert.That(r.Sql, Is.EqualTo("CALL shop.add_order(customer_id => @c)"));
    }

    [Test]
    public void ProceduralSqlInACommandIsReported()
    {
        var r = T("DECLARE @x int = 1; SELECT @x");
        Assert.That(r.Diagnostics.Select(d => d.Code), Does.Contain(DiagnosticCodes.ProceduralSqlInCommand));
    }
}
