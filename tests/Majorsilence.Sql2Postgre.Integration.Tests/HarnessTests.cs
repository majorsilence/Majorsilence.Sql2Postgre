using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

namespace Majorsilence.Sql2Postgre.Integration.Tests;

[Category("Containers")]
public class HarnessTests
{
    [Test]
    public Task SelectOneAgrees() =>
        BothEngines.AssertSameResultAsync("SELECT 1 AS n", "SELECT 1 AS n");

    [Test]
    public Task SetupRunsAndRollsBack() =>
        BothEngines.AssertSameResultAsync(
            "SELECT v FROM t",
            "SELECT v FROM t",
            tsqlSetup: "CREATE TABLE t (v int); INSERT INTO t VALUES (2), (1);",
            pgSetup: "CREATE TABLE t (v integer); INSERT INTO t VALUES (1), (2);");

    [Test]
    public async Task DifferentRowsAreReported()
    {
        var sql = await BothEngines.RunSqlServerAsync("SELECT 1 AS n");
        var pg = await BothEngines.RunPostgresAsync("SELECT 2 AS n");
        Assert.That(pg.Rows, Is.Not.EqualTo(sql.Rows));
    }

    [TestCase("SELECT a FROM t ORDER BY a", true)]
    [TestCase("SELECT a FROM (SELECT TOP 5 a FROM t ORDER BY a) x", false)]
    [TestCase("SELECT a FROM t", false)]
    public void OrderDetection(string sql, bool ordered) =>
        Assert.That(BothEngines.HasTopLevelOrderBy(sql), Is.EqualTo(ordered));
}
