using Majorsilence.Sql2Postgre.Cli;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Tests.Postgres;

public class ScriptTranslatorTests
{
    private static SemanticModel Shop() => SemanticModel.Build(SqlServerScriptReader.Read([("shop.sql",
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")))]).Database);

    private static ScriptTranslation T(string tsql) => ScriptTranslator.Translate(tsql, Shop(), new ConversionOptions());

    [Test]
    public void SessionStatementsAndUseAreDropped()
    {
        var r = T("USE shopdb;\nGO\nSET NOCOUNT ON;\nSET ANSI_NULLS ON;\nSET DATEFORMAT ymd;\nGO\nSELECT name FROM shop.customer;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Is.EqualTo("SELECT name\nFROM shop.customer;\n\n"));
            Assert.That(r.Diagnostics.Select(d => d.Code), Is.All.EqualTo(DiagnosticCodes.SessionStatementDropped));
            Assert.That(r.Diagnostics, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public void TempTableIsKnownAcrossBatches()
    {
        var r = T("SELECT customer_id, name INTO #c FROM shop.customer;\nGO\nSELECT name FROM #c WHERE customer_id > 1;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.Contain("CREATE TEMP TABLE c AS").And.Contain("FROM c\nWHERE customer_id > 1"));
            Assert.That(r.Diagnostics.Where(d => d.Code == DiagnosticCodes.UnresolvedColumn), Is.Empty);
        });
    }

    [Test]
    public void IdentityInsertOffMovesTheSequence()
    {
        var r = T("SET IDENTITY_INSERT shop.customer ON;\nINSERT INTO shop.customer (customer_id, CustomerCode, name) VALUES (500, 'X', N'x');\nSET IDENTITY_INSERT shop.customer OFF;");
        Assert.That(r.Sql, Does.Contain("SELECT setval(pg_get_serial_sequence('shop.customer', 'customer_id'), COALESCE((SELECT max(customer_id) FROM shop.customer), 1));"));
        Assert.That(r.Diagnostics.Count(d => d.Code == DiagnosticCodes.IdentityInsert), Is.EqualTo(2));
    }

    [Test]
    public void BatchWithVariablesIsADoBlockAndKeepsWhatItCannotTranslate()
    {
        var r = T("CREATE TABLE dbo.x (id int);\nDECLARE @n int = 1;\nEXEC shop.add_order 1, NULL;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("DO $$\n#variable_conflict use_column\nDECLARE\n    v_n integer;\nBEGIN\n"));
            Assert.That(r.Sql, Does.Contain("    /* sql2pg:untranslated S2P0205\n    CREATE TABLE dbo.x (id int);"));
            Assert.That(r.Sql, Does.Contain("    v_n := 1;\n"));
            Assert.That(r.Sql, Does.Contain("    /* sql2pg:untranslated S2P0206\n    EXEC shop.add_order 1, NULL;"));
            Assert.That(r.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCodes.DdlInScript, DiagnosticCodes.ProceduralStatement }));
        });
    }

    [Test]
    public void DdlStaysAsAComment()
    {
        var r = T("CREATE TABLE dbo.x (id int);");
        Assert.That(r.Sql, Does.StartWith("/* sql2pg:untranslated S2P0205\nCREATE TABLE dbo.x (id int);"));
    }

    [Test]
    public void UntranslatableStatementIsKeptAndTheRestTranslated()
    {
        var r = T("SELECT name FROM shop.customer FOR XML RAW;\nSELECT 1 AS one;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("/* sql2pg:untranslated S2P4010\n"));
            Assert.That(r.Sql, Does.EndWith("SELECT 1 AS one;\n\n"));
        });
    }

    [Test]
    public void CompatHelpersTheScriptNeedsAreCreatedFirst()
    {
        var r = T("SELECT DATEDIFF(day, order_date, GETDATE()) AS age FROM shop.[order];");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("CREATE SCHEMA IF NOT EXISTS mssql_compat;\n"));
            Assert.That(r.Sql, Does.Contain("CREATE OR REPLACE FUNCTION mssql_compat.datediff("));
            Assert.That(r.Sql.IndexOf("mssql_compat.datediff('day'", StringComparison.Ordinal), Is.GreaterThan(r.Sql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ParseErrorKeepsTheWholeScript()
    {
        var r = T("SELECT FROM WHERE;");
        Assert.That(r.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.ParseError));
        Assert.That(r.Sql, Does.Contain("SELECT FROM WHERE;"));
    }

    [Test]
    public async Task TranslateVerbMirrorsAFolder()
    {
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"translate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "in", "reports"));
        await File.WriteAllTextAsync(Path.Combine(dir, "in", "reports", "top.sql"), "SELECT TOP 5 name FROM shop.customer ORDER BY name;");
        await File.WriteAllTextAsync(Path.Combine(dir, "in", "bad.sql"), "SELECT * FROM otherdb.dbo.t;");
        var output = new StringWriter();
        int code = await new Sql2PgCli(output, new StringWriter()).InvokeAsync(["translate", Path.Combine(dir, "in"), "--out", Path.Combine(dir, "out"),
            "--source", Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql")]);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.CompletedWithProblems), "bad.sql has an error");
            Assert.That(File.ReadAllText(Path.Combine(dir, "out", "reports", "top.pgsql")), Does.Contain("LIMIT 5"));
            Assert.That(File.ReadAllText(Path.Combine(dir, "out", "bad.diag.json")), Does.Contain("S2P0301"));
            Assert.That(output.ToString(), Does.Contain("1 error(s)"));
        });
        Directory.Delete(dir, recursive: true);
    }
}
