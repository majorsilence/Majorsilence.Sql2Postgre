using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// The xml methods in views and queries, converted by sql2pg, on both engines: value() with
/// default and prefixed namespaces, relative and absolute paths, attributes, text() and
/// string(); nodes() nested and under OUTER APPLY (a NULL document); exist() and query().
/// </summary>
[Category("Containers")]
public class XmlMethodBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Candidate (CandidateId int NOT NULL PRIMARY KEY, Resume xml NULL, Contact xml NULL);
        GO
        CREATE TABLE dbo.Model (ModelId int NOT NULL PRIMARY KEY, Instructions xml NULL);
        GO
        INSERT INTO dbo.Candidate VALUES
        (1, N'<Resume xmlns="urn:resume"><Name><Name.First>Ann &amp; Co</Name.First></Name><Address><Addr.Type>Home</Addr.Type></Address><Years>3</Years></Resume>',
            N'<ci:AdditionalContactInfo xmlns:ci="urn:ci" xmlns:act="urn:act"><act:telephoneNumber><act:number>555-0100</act:number></act:telephoneNumber><act:eMail><act:SpecialInstructions>  call first  </act:SpecialInstructions></act:eMail></ci:AdditionalContactInfo>'),
        (2, N'<Resume xmlns="urn:resume"><Name><Name.First>Bob</Name.First></Name><Address><Addr.Type>Work</Addr.Type></Address><Address><Addr.Type>Home</Addr.Type></Address><Years>1</Years></Resume>', NULL),
        (3, N'<Resume xmlns="urn:resume"><Name/></Resume>', N'<ci:AdditionalContactInfo xmlns:ci="urn:ci"/>');
        GO
        INSERT INTO dbo.Model VALUES
        (1, N'<root xmlns="urn:m">Intro text<Location LocationID="10" LaborHours="2.5"><step>Cut <b>it</b>.</step><step>Glue.</step></Location><Location LocationID="20"><step>Paint.</step></Location></root>'),
        (2, NULL);
        GO
        CREATE VIEW dbo.vCandidate AS
        SELECT c.CandidateId
            ,[Resume].ref.value(N'declare default element namespace "urn:resume";
                (/Resume/Name/Name.First)[1]', 'nvarchar(30)') AS [Name.First]
            ,[Resume].ref.value(N'declare default element namespace "urn:resume";
                (Address/Addr.Type)[1]', 'nvarchar(30)') AS [Addr.Type]
            ,[Resume].ref.[value](N'declare default element namespace "urn:resume"; Years[1]', 'integer') AS Years
        FROM dbo.Candidate c
        CROSS APPLY c.Resume.nodes(N'declare default element namespace "urn:resume"; /Resume') AS Resume(ref);
        GO
        CREATE VIEW dbo.vContact AS
        SELECT CandidateId
            ,[ContactInfo].ref.value(N'declare namespace act="urn:act"; ((act:telephoneNumber)[1]/act:number)[1]', 'nvarchar(50)') AS Phone
            ,LTRIM(RTRIM([ContactInfo].ref.value(N'declare namespace act="urn:act";
                (act:eMail/act:SpecialInstructions/text())[1]', 'nvarchar(max)'))) AS Note
        FROM dbo.Candidate
        OUTER APPLY Contact.nodes('declare namespace ci="urn:ci"; /ci:AdditionalContactInfo') AS ContactInfo(ref);
        GO
        CREATE VIEW dbo.vSteps AS
        SELECT ModelId
            ,[Instructions].value(N'declare default element namespace "urn:m"; (/root/text())[1]', 'nvarchar(max)') AS Intro
            ,[Loc].ref.value('@LocationID[1]', 'int') AS LocationID
            ,[Loc].ref.value('@LaborHours[1]', 'decimal(9, 4)') AS LaborHours
            ,[Steps].ref.value('string(.)[1]', 'nvarchar(1024)') AS Step
        FROM dbo.Model
        CROSS APPLY [Instructions].nodes(N'declare default element namespace "urn:m"; /root/Location') Loc(ref)
        CROSS APPLY [Loc].ref.nodes('declare default element namespace "urn:m"; step') Steps(ref);
        GO
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "xmlmethods");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("xmlmethods");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [TestCase("SELECT * FROM dbo.vCandidate ORDER BY CandidateId")]
    [TestCase("SELECT * FROM dbo.vContact ORDER BY CandidateId")]
    [TestCase("SELECT * FROM dbo.vSteps ORDER BY ModelId, LocationID, Step")]
    [TestCase("SELECT CandidateId FROM dbo.Candidate WHERE Resume.exist('declare default element namespace \"urn:resume\"; /Resume/Address[Addr.Type = \"Home\" and ../Years > 2]') = 1")]
    [TestCase("SELECT CandidateId, Contact.exist('declare namespace act=\"urn:act\"; //act:number') AS has_number FROM dbo.Candidate ORDER BY CandidateId")]
    [TestCase("SELECT CandidateId, CAST(Resume.query('declare default element namespace \"urn:resume\"; /Resume/Address/Addr.Type/text()') AS nvarchar(max)) AS types FROM dbo.Candidate ORDER BY CandidateId")]
    [TestCase("SELECT CandidateId, Resume.value('declare default element namespace \"urn:resume\"; count(/Resume/Address)', 'int') AS addresses FROM dbo.Candidate ORDER BY CandidateId")]
    public async Task SameRows(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }
}
