using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Tests.Postgres;

public class PgWriterPlpgsqlTests
{
    private static readonly PgName Orders = new("sales", "order");

    private static string P(PgPlStatement s) => PgWriter.PlStatement(s, 18);

    private static string W(PgStatement s) => PgWriter.Write(s, 18);

    private static PgColumnRef V(string name) => new(name);

    private static PgLiteral N(int n) => new(n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static PgSelect SelectFromOrders(params PgTarget[] targets) => new()
    {
        Targets = targets,
        From = [new PgTableRef(Orders)],
        Where = new PgBinary("=", new PgColumnRef("order_id"), new PgColumnRef("id", "f")),
    };

    [Test]
    public void BlockWithDeclarationsLabelAndHandler()
    {
        var block = new PgBlock([new PgAssign(V("v_x"), new PgBinary("+", V("v_x"), N(1)))])
        {
            Label = "main_block",
            Declarations = [new PgDeclare("v_x", "integer", N(1)), new PgDeclare("v_limit", "integer", N(10), Constant: true, NotNull: true)],
            Handlers = [new PgExceptionHandler(["division_by_zero", "SQLSTATE '22003'"], [PgNullStatement.Instance]), new PgExceptionHandler(["OTHERS"], [PgRaise.Rethrow])],
        };
        Assert.That(P(block), Is.EqualTo("""
            <<main_block>>
            DECLARE
                v_x integer := 1;
                v_limit CONSTANT integer NOT NULL := 10;
            BEGIN
                v_x := v_x + 1;
            EXCEPTION
                WHEN division_by_zero OR SQLSTATE '22003' THEN
                    NULL;
                WHEN OTHERS THEN
                    RAISE;
            END main_block;

            """.ReplaceLineEndings("\n")));
    }

    [Test]
    public void EmptyBlock() => Assert.That(P(new PgBlock([])), Is.EqualTo("BEGIN\nEND;\n"));

    [Test]
    public void IfElsifElseNested()
    {
        var s = new PgIf([
            new PgIfBranch(new PgBinary(">", V("v_x"), N(0)), [new PgIf([new PgIfBranch(new PgIsNull(V("v_y")), [new PgReturn(N(1))])])]),
            new PgIfBranch(new PgBinary("<", V("v_x"), N(0)), [new PgReturn(N(-1))]),
        ], [new PgReturn(N(0))]);
        Assert.That(P(s), Is.EqualTo("""
            IF v_x > 0 THEN
                IF v_y IS NULL THEN
                    RETURN 1;
                END IF;
            ELSIF v_x < 0 THEN
                RETURN -1;
            ELSE
                RETURN 0;
            END IF;

            """.ReplaceLineEndings("\n")));
    }

    [Test]
    public void Loops()
    {
        Assert.That(P(new PgWhile(new PgBinary("<", V("v_i"), N(10)), [
                new PgAssign(V("v_i"), new PgBinary("+", V("v_i"), N(1))),
                new PgContinue(When: new PgBinary("=", V("v_i"), N(5))),
                new PgExit("l", new PgBinary(">", V("v_i"), N(8))),
            ], "l")),
            Is.EqualTo("<<l>>\nWHILE v_i < 10 LOOP\n    v_i := v_i + 1;\n    CONTINUE WHEN v_i = 5;\n    EXIT l WHEN v_i > 8;\nEND LOOP l;\n"));
        Assert.That(P(new PgLoop([new PgExit()])), Is.EqualTo("LOOP\n    EXIT;\nEND LOOP;\n"));
        Assert.That(P(new PgForRange("i", N(1), V("v_n"), [PgNullStatement.Instance]) { Reverse = true, Step = N(2) }),
            Is.EqualTo("FOR i IN REVERSE 1 .. v_n BY 2 LOOP\n    NULL;\nEND LOOP;\n"));
    }

    [Test]
    public void ForOverQueryAndDynamicQuery()
    {
        var query = SelectFromOrders(new PgTarget(new PgColumnRef("total")));
        Assert.That(P(new PgForQuery("r", query, [new PgAssign(V("v_sum"), new PgBinary("+", V("v_sum"), new PgColumnRef("total", "r")))])),
            Is.EqualTo("FOR r IN SELECT total\n    FROM sales.\"order\"\n    WHERE order_id = f.id\nLOOP\n    v_sum := v_sum + r.total;\nEND LOOP;\n"));
        Assert.That(P(new PgForQuery("r", null, [PgNullStatement.Instance]) { Execute = V("v_sql"), Using = [V("v_a")] }),
            Is.EqualTo("FOR r IN EXECUTE v_sql USING v_a\nLOOP\n    NULL;\nEND LOOP;\n"));
    }

    [Test]
    public void Returns()
    {
        Assert.That(P(new PgReturn()), Is.EqualTo("RETURN;\n"));
        Assert.That(P(new PgReturnNext()), Is.EqualTo("RETURN NEXT;\n"));
        Assert.That(P(new PgReturnNext(V("r"))), Is.EqualTo("RETURN NEXT r;\n"));
        Assert.That(P(new PgReturnQuery(new PgSelect { Targets = [new PgTarget(N(1))] })), Is.EqualTo("RETURN QUERY SELECT 1;\n"));
        Assert.That(P(new PgReturnQuery(null) { Execute = PgLiteral.String("SELECT $1"), Using = [N(2)] }),
            Is.EqualTo("RETURN QUERY EXECUTE 'SELECT $1' USING 2;\n"));
    }

    [Test]
    public void Raise()
    {
        Assert.That(P(new PgRaise("NOTICE", "%") { Arguments = [V("v_msg")] }), Is.EqualTo("RAISE NOTICE '%', v_msg;\n"));
        Assert.That(P(new PgRaise("EXCEPTION", "it's %% done: %") { Arguments = [N(5)], Using = [new("ERRCODE", PgLiteral.String("P0001"))] }),
            Is.EqualTo("RAISE EXCEPTION 'it''s %% done: %', 5 USING ERRCODE = 'P0001';\n"));
        Assert.That(P(new PgRaise("EXCEPTION") { Using = [new("MESSAGE", V("v_msg")), new("DETAIL", PgLiteral.String("mssql_error=50001"))] }),
            Is.EqualTo("RAISE EXCEPTION USING MESSAGE = v_msg, DETAIL = 'mssql_error=50001';\n"));
        Assert.That(P(PgRaise.Rethrow), Is.EqualTo("RAISE;\n"));
    }

    [Test]
    public void RaiseWithWrongArgumentCountIsRejected()
    {
        Assert.Throws<ArgumentException>(() => P(new PgRaise("NOTICE", "% and %") { Arguments = [N(1)] }));
        Assert.Throws<ArgumentException>(() => P(new PgRaise("NOTICE")));
        Assert.Throws<ArgumentException>(() => P(new PgRaise(null, "x")));
    }

    [Test]
    public void PerformReplacesSelectOrWrapsTheQuery()
    {
        Assert.That(P(new PgPerform(SelectFromOrders(new PgTarget(new PgFunctionCall("pg_notify", [PgLiteral.String("c"), PgLiteral.String("m")]))))),
            Is.EqualTo("PERFORM pg_notify('c', 'm')\n    FROM sales.\"order\"\n    WHERE order_id = f.id;\n"));
        var union = new PgSetOperation("UNION ALL", new PgSelect { Targets = [new PgTarget(N(1))] }, new PgSelect { Targets = [new PgTarget(N(2))] });
        Assert.That(P(new PgPerform(union)), Is.EqualTo("PERFORM FROM (SELECT 1 UNION ALL SELECT 2) AS q;\n"));
    }

    [Test]
    public void DynamicSqlAndDiagnostics()
    {
        Assert.That(P(new PgExecuteDynamic(new PgFunctionCall("format", [PgLiteral.String("SELECT count(*) FROM %I"), V("v_t")])) { Into = [V("v_n")], Strict = true, Using = [V("v_a"), V("v_b")] }),
            Is.EqualTo("EXECUTE format('SELECT count(*) FROM %I', v_t) INTO STRICT v_n USING v_a, v_b;\n"));
        Assert.That(P(new PgExecuteDynamic(PgLiteral.String("TRUNCATE t"))), Is.EqualTo("EXECUTE 'TRUNCATE t';\n"));
        Assert.That(P(new PgGetDiagnostics([new("v_rowcount", "ROW_COUNT")])), Is.EqualTo("GET DIAGNOSTICS v_rowcount = ROW_COUNT;\n"));
        Assert.That(P(new PgGetDiagnostics([new("v_error_message", "MESSAGE_TEXT"), new("v_state", "RETURNED_SQLSTATE")], Stacked: true)),
            Is.EqualTo("GET STACKED DIAGNOSTICS v_error_message = MESSAGE_TEXT, v_state = RETURNED_SQLSTATE;\n"));
    }

    [Test]
    public void Cursors()
    {
        var block = new PgBlock([])
        {
            Declarations = [
                new PgDeclareCursor("c", SelectFromOrders(new PgTarget(new PgColumnRef("total"))), Scroll: true) { Arguments = [new("id", "integer")] },
                new PgDeclare("rc", "refcursor"),
            ],
        };
        Assert.That(P(block), Is.EqualTo("DECLARE\n    c SCROLL CURSOR (id integer) FOR SELECT total\n        FROM sales.\"order\"\n        WHERE order_id = f.id;\n    rc refcursor;\nBEGIN\nEND;\n"));
        Assert.That(P(new PgOpenCursor("c") { Arguments = [N(7)] }), Is.EqualTo("OPEN c(7);\n"));
        Assert.That(P(new PgOpenCursor("c")), Is.EqualTo("OPEN c;\n"));
        Assert.That(P(new PgOpenCursor("rc") { Query = new PgSelect { Targets = [new PgTarget(N(1))] }, Scroll = false }), Is.EqualTo("OPEN rc NO SCROLL FOR SELECT 1;\n"));
        Assert.That(P(new PgOpenCursor("rc") { Execute = V("v_sql"), Using = [N(1)] }), Is.EqualTo("OPEN rc FOR EXECUTE v_sql USING 1;\n"));
        Assert.That(P(new PgFetch("c", [V("v_a"), V("v_b")])), Is.EqualTo("FETCH NEXT FROM c INTO v_a, v_b;\n"));
        Assert.That(P(new PgFetch("c", [V("v_a")], "ABSOLUTE") { Count = N(3) }), Is.EqualTo("FETCH ABSOLUTE 3 FROM c INTO v_a;\n"));
        Assert.That(P(new PgFetch("c", [], "PRIOR") { Move = true }), Is.EqualTo("MOVE PRIOR FROM c;\n"));
        Assert.Throws<ArgumentException>(() => P(new PgFetch("c", [])));
        Assert.That(P(new PgClose("c")), Is.EqualTo("CLOSE c;\n"));
    }

    [Test]
    public void TransactionsCallsAndAsserts()
    {
        Assert.That(P(new PgTransactionControl(true)), Is.EqualTo("COMMIT;\n"));
        Assert.That(P(new PgTransactionControl(false, AndChain: true)), Is.EqualTo("ROLLBACK AND CHAIN;\n"));
        Assert.That(P(new PgCall(new PgName("sales", "add order"), [new(N(1)), new(V("v_x"), "order")])),
            Is.EqualTo("CALL sales.\"add order\"(1, \"order\" => v_x);\n"));
        Assert.That(P(new PgAssert(new PgBinary(">", V("v_x"), N(0)), PgLiteral.String("x must be positive"))),
            Is.EqualTo("ASSERT v_x > 0, 'x must be positive';\n"));
    }

    [Test]
    public void SqlStatementsWithInto()
    {
        var select = new PgSqlStatement(new PgQueryStatement(SelectFromOrders(new PgTarget(new PgColumnRef("total")), new PgTarget(new PgColumnRef("status")))))
        {
            Into = [V("v_total"), V("v_status")],
        };
        Assert.That(P(select), Is.EqualTo("SELECT total, status\nINTO v_total, v_status\nFROM sales.\"order\"\nWHERE order_id = f.id;\n"));

        var insert = new PgSqlStatement(new PgInsert(Orders, ["total"], new PgValues([[N(5)]])) { Returning = [new PgTarget(new PgColumnRef("order_id"))] })
        {
            Into = [V("v_scope_identity")],
            Strict = true,
        };
        Assert.That(P(insert), Is.EqualTo("INSERT INTO sales.\"order\" (total)\nVALUES (5)\nRETURNING order_id\nINTO STRICT v_scope_identity;\n"));

        Assert.That(P(new PgSqlStatement(new PgDelete(Orders, null))), Is.EqualTo("DELETE FROM sales.\"order\";\n"));
        Assert.Throws<ArgumentException>(() => P(new PgSqlStatement(new PgCreateSchema("s")) { Into = [V("v_x")] }));
    }

    [Test]
    public void NestingIndentsCodeButNotStringLiterals()
    {
        var insert = new PgInsert(Orders, ["note"], new PgValues([[PgLiteral.String("line one\nline two")]]));
        var loop = new PgLoop([new PgSqlStatement(insert), new PgSqlStatement(new PgUntranslated("S2P9001", "PRINT 'it''s'\n/* inner */")), new PgExit()]);
        Assert.That(P(new PgBlock([loop])), Is.EqualTo("""
            BEGIN
                LOOP
                    INSERT INTO sales."order" (note)
                    VALUES ('line one
            line two');
                    /* sql2pg:untranslated S2P9001
                    PRINT 'it''s'
                    / * inner * /
                    */
                    EXIT;
                END LOOP;
            END;

            """.ReplaceLineEndings("\n")));
    }

    [Test]
    public void IndentLinesFollowsEveryQuotingForm()
    {
        const string text = "a 'x\ny' \"q\nr\" E'it\\'s\nz' -- don't\nb $t$ p\nq $t$ /* it's\nc */ d\n";
        Assert.That(PgWriter.IndentLines(text, ">"), Is.EqualTo(">a 'x\ny' \"q\nr\" E'it\\'s\nz' -- don't\n>b $t$ p\nq $t$ /* it's\n>c */ d\n"));
    }

    [Test]
    public void PlpgsqlFunction()
    {
        var f = new PgCreateRoutine(new PgName("sales", "order_total"), false, [new PgParameter("order_id", "integer"), new PgParameter("tax", "numeric", Default: N(0))],
            new PgPlBody(new PgBlock([new PgReturn(new PgBinary("+", new PgColumnRef("order_id", "order_total"), new PgColumnRef("tax", "order_total")))]))
            {
                Directives = ["#variable_conflict use_column"],
            })
        {
            Returns = "numeric",
            Volatility = "STABLE",
        };
        Assert.That(W(f), Is.EqualTo("""
            CREATE FUNCTION sales.order_total(order_id integer, tax numeric DEFAULT 0)
            RETURNS numeric
            LANGUAGE plpgsql
            STABLE
            AS $$
            #variable_conflict use_column
            BEGIN
                RETURN order_total.order_id + order_total.tax;
            END;
            $$;

            """.ReplaceLineEndings("\n")));
    }

    [Test]
    public void ProcedureWithInoutAndReturnsTableFunction()
    {
        var p = new PgCreateRoutine(new PgName("sales", "next_no"), true, [new PgParameter("seed", "integer"), new PgParameter("result", "integer", "INOUT", PgLiteral.Null)],
            new PgPlBody(new PgBlock([new PgAssign(new PgColumnRef("result", "next_no"), new PgColumnRef("seed", "next_no"))])))
        {
            OrReplace = true,
            Returns = "ignored",
            Volatility = "STABLE",
        };
        Assert.That(W(p), Is.EqualTo("CREATE OR REPLACE PROCEDURE sales.next_no(seed integer, INOUT result integer DEFAULT NULL)\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    next_no.result := next_no.seed;\nEND;\n$$;\n"));

        var f = new PgCreateRoutine(new PgName("sales", "lines"), false, [],
            new PgSqlBody([new PgQueryStatement(SelectFromOrders(new PgTarget(new PgColumnRef("order_id")), new PgTarget(new PgColumnRef("total"))))]))
        {
            ReturnsTable = [new("order_id", "integer"), new("total", "numeric(19,4)")],
            Volatility = "STABLE",
        };
        Assert.That(W(f), Is.EqualTo("CREATE FUNCTION sales.lines()\nRETURNS TABLE (order_id integer, total numeric(19,4))\nLANGUAGE sql\nSTABLE\nAS $$\nSELECT order_id, total\nFROM sales.\"order\"\nWHERE order_id = f.id;\n$$;\n"));
    }

    [Test]
    public void SqlStandardReturnBody()
    {
        var f = new PgCreateRoutine(new PgName("dbo", "add1"), false, [new PgParameter("x", "integer")], new PgSqlReturnBody(new PgBinary("+", new PgColumnRef("x"), N(1))))
        {
            Returns = "integer",
            Volatility = "IMMUTABLE",
        };
        Assert.That(W(f), Is.EqualTo("CREATE FUNCTION dbo.add1(x integer)\nRETURNS integer\nLANGUAGE sql\nIMMUTABLE\nRETURN x + 1;\n"));
        Assert.Throws<ArgumentException>(() => W(f with { Procedure = true }));
        Assert.Throws<ArgumentException>(() => W(f with { Returns = null }));
    }

    [Test]
    public void DollarTagAvoidsTheBodysOwnText()
    {
        var body = new PgPlBody(new PgBlock([new PgReturn(PgLiteral.String("costs $$5 or $body$"))]));
        var f = new PgCreateRoutine(new PgName("dbo", "price"), false, [], body) { Returns = "text" };
        Assert.That(W(f), Does.Contain("AS $body1$\nBEGIN\n    RETURN 'costs $$5 or $body$';\nEND;\n$body1$;\n"));
        Assert.That(W(f with { Body = new PgPlBody(new PgBlock([new PgReturn(PgLiteral.String("$$"))])) }), Does.Contain("AS $body$\n"));
    }

    [Test]
    public void UntranslatedCommentCannotNestOrEndEarly() =>
        Assert.That(W(new PgUntranslated("S2P9001", "SELECT 1 /* a */ /*")), Is.EqualTo("/* sql2pg:untranslated S2P9001\nSELECT 1 / * a * / / *\n*/\n"));
}
