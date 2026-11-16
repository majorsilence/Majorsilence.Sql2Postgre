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
        var r = T("CREATE TABLE dbo.x (id int);\nDECLARE @n int = 1;\nEXEC shop.add_order 1, NULL;\nEXEC shop.missing 1;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("DO $$\n#variable_conflict use_column\nDECLARE\n    v_n integer;\n    sql2pg_arg1 bigint;\nBEGIN\n"));
            Assert.That(r.Sql, Does.Contain("    /* sql2pg:untranslated S2P0205\n    CREATE TABLE dbo.x (id int);"));
            Assert.That(r.Sql, Does.Contain("    v_n := 1;\n"));
            // NULL for the OUTPUT parameter, not taken back: PL/pgSQL still needs a variable for it.
            Assert.That(r.Sql, Does.Contain("    sql2pg_arg1 := NULL;\n    CALL shop.add_order(customer_id => 1, order_id => sql2pg_arg1);\n"));
            Assert.That(r.Sql, Does.Contain("    /* sql2pg:untranslated S2P5027\n    EXEC shop.missing 1;"));
            Assert.That(r.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCodes.DdlInScript, DiagnosticCodes.UnknownProcedure }));
        });
    }

    [Test]
    public void InsertExecOfAProcedureThatIsNotAFunctionStaysAComment()
    {
        var r = T("INSERT INTO shop.customer (name) EXEC shop.add_order 1;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("/* sql2pg:untranslated S2P4003\n"));
            Assert.That(r.Diagnostics.Single().Message, Does.Contain("not a set-returning function"));
        });
    }

    [Test]
    public void PlainExecAndTransactionsAreTopLevelStatements()
    {
        var r = T("BEGIN TRAN t1;\nEXEC shop.add_order @customer_id = 7;\nSAVE TRAN s1;\nROLLBACK TRAN s1;\nCOMMIT TRAN t1;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Is.EqualTo("BEGIN;\n\nCALL shop.add_order(customer_id => 7);\n\nSAVEPOINT s1;\n\nROLLBACK TO SAVEPOINT s1;\n\nCOMMIT;\n\n"));
            Assert.That(r.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void TableVariableOutsideADoBlockLivesAsLongAsTheBatch()
    {
        var r = T("DECLARE @t TABLE (id int PRIMARY KEY, name nvarchar(50));\nINSERT INTO @t (id, name) SELECT customer_id, name FROM shop.customer;\nSELECT name FROM @t;\nGO\nSELECT 1 AS one;");
        Assert.Multiple(() =>
        {
            Assert.That(r.Sql, Does.StartWith("DROP TABLE IF EXISTS tv_t;\nCREATE TEMP TABLE tv_t (\n    id integer NOT NULL,\n    name varchar(50)\n);\nALTER TABLE tv_t ADD CONSTRAINT tv_t_pkey PRIMARY KEY (id);\n"));
            Assert.That(r.Sql, Does.Contain("INSERT INTO tv_t (id, name)").And.Contain("SELECT name\nFROM tv_t;"));
            Assert.That(r.Sql, Does.EndWith("FROM tv_t;\n\nDROP TABLE IF EXISTS tv_t;\n\nSELECT 1 AS one;\n\n"));
            Assert.That(r.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void TempTableDdlIsTranslatedAndGlobalTempTablesAreNot()
    {
        var r = T("CREATE TABLE #t (id int IDENTITY, name nvarchar(50));\nCREATE INDEX ix ON #t (name);\nTRUNCATE TABLE #t;\nDROP TABLE IF EXISTS #t;\nCREATE TABLE ##g (id int);");
        Assert.Multiple(() =>
        {
            // A script's CREATE fails when the table exists, as SQL Server's does: no DROP before it.
            Assert.That(r.Sql, Does.StartWith("CREATE TEMP TABLE t (\n    id integer GENERATED BY DEFAULT AS IDENTITY (START WITH 1 INCREMENT BY 1),\n    name varchar(50)\n);\n"));
            Assert.That(r.Sql, Does.Contain("CREATE INDEX ON t (name);\n").And.Contain("TRUNCATE TABLE t RESTART IDENTITY;\n").And.Contain("DROP TABLE IF EXISTS t;\n"));
            Assert.That(r.Sql, Does.Contain("/* sql2pg:untranslated S2P5030\nCREATE TABLE ##g (id int);"));
            Assert.That(r.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCodes.GlobalTempTable }));
        });
    }

    [Test]
    public void TableVariableInADoBlockIsCreatedFirstAndDroppedLast()
    {
        var r = T("DECLARE @n int = 0;\nWHILE @n < 2\nBEGIN\n    DECLARE @t TABLE (n int);\n    INSERT INTO @t (n) VALUES (@n);\n    SET @n += 1;\nEND;\nUPDATE shop.customer SET name = name OUTPUT inserted.customer_id INTO @t WHERE customer_id = 1;");
        Assert.Multiple(() =>
        {
            // Declared in the loop, created once before it: T-SQL keeps the rows across iterations.
            Assert.That(r.Sql, Does.Contain("BEGIN\n    DROP TABLE IF EXISTS tv_t;\n    CREATE TEMP TABLE tv_t (\n        n integer\n    );\n    WHILE v_n < 2 LOOP\n"));
            Assert.That(r.Sql, Does.Contain("    WITH sql2pg_output (c1) AS (UPDATE shop.customer\n"));
            Assert.That(r.Sql, Does.Contain("    INSERT INTO tv_t\n    SELECT c1\n    FROM sql2pg_output;\n    DROP TABLE IF EXISTS tv_t;\nEND;\n$$;"));
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
