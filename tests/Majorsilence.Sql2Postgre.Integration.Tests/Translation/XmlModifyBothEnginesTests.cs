using Majorsilence.Sql2Postgre.Compat;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// WP 4.24: xml modify() over a grid of documents. Each case puts one row in both engines, runs
/// the same UPDATE … SET x.modify('…') (translated for PostgreSQL), and compares the documents'
/// text, or that both fail. The documents are written as SQL Server writes xml (no insignificant
/// whitespace, <c>&lt;a/&gt;</c>), so the two texts start the same.
/// </summary>
[Category("Containers")]
public class XmlModifyBothEnginesTests
{
    private const string Script = """
        CREATE XML SCHEMA COLLECTION dbo.Survey AS N'<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:s" elementFormDefault="qualified"><xsd:element name="S"><xsd:complexType><xsd:complexContent><xsd:restriction base="xsd:anyType"><xsd:sequence><xsd:element name="T" type="xsd:decimal" minOccurs="0"/><xsd:element name="D" type="xsd:double" minOccurs="0"/><xsd:element name="N" type="xsd:string" minOccurs="0"/></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element></xsd:schema>';
        GO
        CREATE TABLE dbo.Doc (Id int NOT NULL PRIMARY KEY, X xml NULL, T xml(dbo.Survey) NULL,
            N numeric(38,6) NULL, I int NULL, F float NULL, S nvarchar(50) NULL, S2 nvarchar(50) NULL, B bit NULL);
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "xmlmodify");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        Assert.That(db.XmlSchemaCollections?.Select(x => x.Name.ToString()), Is.EqualTo(new[] { "dbo.Survey" }));
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("xmlmodify");
        var schema = SchemaConverter.Convert(db, Options);
        var applied = await PgApplier.ApplyAsync(schema.Objects, _target.ConnectionString);
        Assert.That(applied.Failed, Is.Zero);
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS mssql_compat;\n" + CompatScripts.Create("xml_modify", "mssql_compat"), conn).ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    private const string Ns = "declare default element namespace \"urn:s\"; ";

    /// <summary>(name, X, T, SET clause): the row has N = 12.5, I = 3, F = 0.1, S = q&lt;&amp;&gt;"', S2 NULL, B 1.</summary>
    private static readonly (string Name, string? X, string? T, string Set)[] Cases =
    [
        ("first", "<a><b/></a>", null, X("insert <c>1</c> as first into (/a)[1]")),
        ("last", "<a><b/></a>", null, X("insert <c>1</c> as last into (/a)[1]")),
        ("into", "<a><b/></a>", null, X("insert <c>1</c> into (/a)[1]")),
        ("into an empty element", "<a/>", null, X("insert <c>1</c> into (/a)[1]")),
        ("into an empty element with attributes", "<a k=\"v\"/>", null, X("insert <c>1</c> as first into (/a)[1]")),
        ("no target", "<a><b/></a>", null, X("insert <c>1</c> into (/x)[1]")),
        ("the second match", "<a><b/><b/></a>", null, X("insert <c>1</c> as first into (/a/b)[2]")),
        ("a predicate", "<a><b k=\"1\"/><b k=\"2\"/></a>", null, X("insert <c/> as last into (/a/b[@k=\"2\"])[1]")),
        ("before", "<a><b/><c/></a>", null, X("insert <z/> before (/a/c)[1]")),
        ("after", "<a><b>x</b><c/></a>", null, X("insert <z/> after (/a/b)[1]")),
        ("after an empty element", "<a><b/><c/></a>", null, X("insert <z/> after (/a/b)[1]")),
        ("another default namespace in scope", "<a xmlns=\"urn:x\"><b/></a>", null, X("insert <c>1</c> as first into (/*)[1]")),
        ("the default namespace", "<a xmlns=\"urn:x\"><b/></a>", null, X("declare default element namespace \"urn:x\"; insert <c>1</c> as first into (/a)[1]")),
        ("after an undeclared default namespace", "<a xmlns=\"urn:x\"><b xmlns=\"\"><c/></b></a>", null,
            X("declare default element namespace \"urn:x\"; insert <z/> after (/a/*[local-name()=\"b\"])[1]")),
        ("a prefixed document", "<p:a xmlns:p=\"urn:x\"><p:b/></p:a>", null, X("declare namespace p=\"urn:x\"; insert <c/> as last into (/p:a/p:b)[1]")),
        ("constructor text", "<a><t>1</t></a>", null, X("insert <z>a &amp; b &lt; c</z> as last into (/a)[1]")),
        ("boundary whitespace", "<a><t>1</t></a>", null, X("insert <z>  <y/>  </z> as last into (/a)[1]")),
        ("significant whitespace", "<a><t>1</t></a>", null, X("insert <z>  x  </z> as last into (/a)[1]")),
        ("a constructor attribute", "<a><t>1</t></a>", null, X("insert <z q='1' r=\"&lt;\"></z> as last into (/a)[1]")),
        ("cdata", "<a><t>1</t></a>", null, X("insert <z><![CDATA[<x>]]></z> as last into (/a)[1]")),
        ("comments and processing instructions", "<a><!--c--><?p d?><t>1</t></a>", null, X("insert <z/> as last into (/a)[1]")),
        ("untyped + decimal", "<a><t>8248.99</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] + sql:column(\"N\")")),
        ("untyped * int", "<a><t>3</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] * sql:column(\"I\")")),
        ("a large double", "<a><t>3</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] * 1000000")),
        ("a small double", "<a><t>3</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] div 10000000")),
        ("a double zero", "<a><t>0</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] * 5")),
        ("two thirds", "<a><t>2</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] div 3")),
        ("a path value", "<a><t>2</t></a>", null, X("replace value of (/a/t/text())[1] with -(/a/t)[1]")),
        ("a float column", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"F\")")),
        ("a float column times 3", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"F\") * 3")),
        ("a double literal", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with 123456.7e0")),
        ("integer arithmetic", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"I\") * 1000000")),
        ("negation", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with -(sql:column(\"I\") + 2) * 3")),
        ("a decimal literal", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with 1.50")),
        ("decimal arithmetic", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"N\") * 2 - 0.5")),
        ("a string column", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"S\")")),
        ("a string literal", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with \"it's &lt;x&gt; &#65;\"\"\"")),
        ("an empty string", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with \"\"")),
        ("a bit", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"B\")")),
        ("a mixed element's value", "<a><t>x</t><u>y<v>w</v></u></a>", null, X("replace value of (/a/t/text())[1] with data(/a/u)[1]")),
        ("the second text node", "<a><t>1</t>x<!--c-->y</a>", null, X("replace value of (/a/text())[2] with \"9\"")),
        ("no text node", "<a><t/></a>", null, X("replace value of (/a/t/text())[1] with \"x\"")),
        ("an attribute", "<a b=\"1\"/>", null, X("replace value of (/a/@b)[1] with sql:column(\"S\")")),
        ("no attribute", "<a b=\"1\"/>", null, X("replace value of (/a/@c)[1] with \"2\"")),
        ("one attribute of several", "<a c=\"2\" b=\"1\"><b/></a>", null, X("replace value of (/a/@b)[1] with \"v\"")),
        ("a null document", null, null, X("insert <c/> into (/a)[1]")),
        ("a null value", "<a><t>1</t></a>", null, X("replace value of (/a/t/text())[1] with sql:column(\"S2\")")),
        ("a value that is not a number", "<a><t>abc</t></a>", null, X("replace value of (/a/t/text())[1] with data(/a/t)[1] + 1")),
        ("typed: insert", null, "<S xmlns=\"urn:s\"><D>1.5</D></S>", T(Ns + "insert <T>0</T> as first into (/S)[1]")),
        ("typed: decimal + decimal", null, "<S xmlns=\"urn:s\"><T>8248.99</T></S>", T(Ns + "replace value of (/S/T)[1] with data(/S/T)[1] + sql:column(\"N\")")),
        ("typed: decimal - decimal", null, "<S xmlns=\"urn:s\"><T>8248.99</T></S>", T(Ns + "replace value of (/S/T)[1] with data(/S/T)[1] - sql:column(\"N\") * 1000")),
        ("typed: an integer", null, "<S xmlns=\"urn:s\"><T>8248.99</T></S>", T(Ns + "replace value of (/S/T)[1] with 5")),
        ("typed: double * decimal", null, "<S xmlns=\"urn:s\"><D>10</D></S>", T(Ns + "replace value of (/S/D)[1] with data(/S/D)[1] * sql:column(\"N\")")),
        ("typed: a string", null, "<S xmlns=\"urn:s\"><N>a</N></S>", T(Ns + "replace value of (/S/N)[1] with sql:column(\"S\")")),
        ("typed: an empty element", null, "<S xmlns=\"urn:s\"><N/></S>", T(Ns + "replace value of (/S/N)[1] with \"q\"")),
        ("typed: an empty string", null, "<S xmlns=\"urn:s\"><N>a</N></S>", T(Ns + "replace value of (/S/N)[1] with \"\"")),
        ("typed: no target", null, "<S xmlns=\"urn:s\"/>", T(Ns + "replace value of (/S/T)[1] with 5")),
    ];

    private static string X(string dml) => $"X.modify('{dml.Replace("'", "''")}')";

    private static string T(string dml) => $"T.modify('{dml.Replace("'", "''")}')";

    private static IEnumerable<TestCaseData> CaseData() => Cases.Select(c => new TestCaseData(c.Name, c.X, c.T, c.Set).SetName($"Modify({c.Name})"));

    [TestCaseSource(nameof(CaseData))]
    public async Task Modify(string name, string? x, string? t, string set)
    {
        string update = $"UPDATE dbo.Doc SET {set}";
        var translation = CommandTranslator.Translate(update, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);

        string sqlServer, postgres;
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await new SqlCommand($"DELETE FROM dbo.Doc; INSERT INTO dbo.Doc VALUES (1, {Literal(x, "N")}, {Literal(t, "N")}, 12.5, 3, 0.1, N'q<&>\"''', NULL, 1);", conn)
                .ExecuteNonQueryAsync();
            try
            {
                await new SqlCommand(update, conn).ExecuteNonQueryAsync();
                sqlServer = Convert.ToString(await new SqlCommand("SELECT CONCAT(CAST(X AS nvarchar(max)), '|', CAST(T AS nvarchar(max))) FROM dbo.Doc", conn).ExecuteScalarAsync())!;
            }
            catch (SqlException)
            {
                sqlServer = "failed";
            }
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"DELETE FROM public.doc; INSERT INTO public.doc VALUES (1, {Literal(x, "")}, {Literal(t, "")}, 12.5, 3, 0.1, 'q<&>\"''', NULL, true);", conn)
                .ExecuteNonQueryAsync();
            try
            {
                await new NpgsqlCommand(translation.Sql, conn).ExecuteNonQueryAsync();
                postgres = Convert.ToString(await new NpgsqlCommand("SELECT concat(x::text, '|', t::text) FROM public.doc", conn).ExecuteScalarAsync())!;
            }
            catch (PostgresException)
            {
                postgres = "failed";
            }
        }
        Assert.That(postgres, Is.EqualTo(sqlServer), $"{name}\n{translation.Sql}");
        Assert.That(sqlServer == "failed", Is.EqualTo(name is "a null document" or "a null value" or "a value that is not a number"), sqlServer);
        TestContext.Out.WriteLine(sqlServer);
    }

    private static string Literal(string? value, string prefix) => value is null ? "NULL" : $"{prefix}'{value.Replace("'", "''")}'";
}
