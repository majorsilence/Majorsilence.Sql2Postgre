using Majorsilence.Sql2Postgre.Cli;

namespace Majorsilence.Sql2Postgre.Tests.Cli;

[NonParallelizable] // the tests clear SQL2PG_SOURCE / SQL2PG_TARGET
public class CliTests
{
    private static string FixturePath => Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "shop.sql");

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await new Sql2PgCli(output, error).InvokeAsync(args);
        return (code, output.ToString(), error.ToString());
    }

    [SetUp]
    public void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable(Sql2PgCli.SourceVariable, null);
        Environment.SetEnvironmentVariable(Sql2PgCli.TargetVariable, null);
    }

    [Test]
    public async Task HelpSucceeds()
    {
        var (code, output, _) = await Run("--help");
        Assert.That(code, Is.EqualTo(ExitCodes.Success));
        Assert.That(output, Does.Contain("assess").And.Contain("migrate"));
    }

    [Test]
    public async Task UnknownVerbIsAUsageError()
    {
        var (code, _, error) = await Run("convert-everything");
        Assert.That(code, Is.EqualTo(ExitCodes.UsageError));
        Assert.That(error, Does.Contain("error:"));
    }

    [Test]
    public async Task MissingRequiredOptionIsAUsageError() =>
        Assert.That((await Run("schema", "--source", FixturePath)).Code, Is.EqualTo(ExitCodes.UsageError), "--out is required");

    [Test]
    public async Task MissingSourceWithoutEnvironmentIsAUsageError()
    {
        var (code, _, error) = await Run("assess");
        Assert.That(code, Is.EqualTo(ExitCodes.UsageError));
        Assert.That(error, Does.Contain(Sql2PgCli.SourceVariable));
    }

    [Test]
    public async Task SourceFromTheEnvironment()
    {
        Environment.SetEnvironmentVariable(Sql2PgCli.SourceVariable, FixturePath);
        var (code, output, _) = await Run("assess");
        Assert.That(code, Is.EqualTo(ExitCodes.CompletedWithProblems).Or.EqualTo(ExitCodes.Success));
        Assert.That(output, Does.Contain("Table"));
    }

    [Test]
    public async Task BadVerifyModeIsAUsageError()
    {
        var (code, _, error) = await Run("data", "--source", "Server=x;Password=secret", "--target", "Host=y", "--verify", "maybe");
        Assert.That(code, Is.EqualTo(ExitCodes.UsageError));
        Assert.That(error, Does.Contain("--verify"));
    }

    [Test]
    public async Task DataNeedsALiveSource() =>
        Assert.That((await Run("data", "--source", FixturePath, "--target", "Host=y")).Code, Is.EqualTo(ExitCodes.UsageError));

    [Test]
    public async Task InvalidConfigIsAUsageError()
    {
        string config = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bad-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(config, """{ "targetVersion": 12, "extensions": ["citext"] }""");
        var (code, _, error) = await Run("assess", "--source", FixturePath, "--config", config);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.UsageError));
            Assert.That(error, Does.Contain("targetVersion").And.Contain("citext"));
        });
        File.Delete(config);
    }

    [Test]
    public async Task AssessFromScriptsSummarizesAndWritesJson()
    {
        string json = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"assess-{Guid.NewGuid():N}.json");
        var (code, output, _) = await Run("assess", "--source", FixturePath, "--json", json);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.Success), "the fixture read from its script converts without Manual objects");
            Assert.That(output, Does.Contain("Table").And.Contain("Trigger").And.Contain("S2P2002"));
            Assert.That(File.ReadAllText(json), Does.Contain("\"conversion\"").And.Contain("\"effort\""));
            Assert.That(output, Does.Contain("Estimated effort:"));
        });
        File.Delete(json);
    }

    [Test]
    public async Task AssessWritesASelfContainedHtmlReport()
    {
        string html = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"assess-{Guid.NewGuid():N}.html");
        var (code, output, _) = await Run("assess", "--source", FixturePath, "--html", html);
        string page = File.ReadAllText(html);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.Success));
            Assert.That(output, Does.Contain("HTML report written to"));
            Assert.That(page, Does.StartWith("<!doctype html>").And.Contain("<table id=\"objects\"").And.Contain("shop.trg_order_line_total"));
            // Self-contained: no script, stylesheet or font fetched from elsewhere.
            Assert.That(page, Does.Not.Contain("<script src").And.Not.Contain("<link").And.Not.Contain("http://").And.Not.Contain("https://"));
            // Names and messages are escaped.
            Assert.That(page, Does.Not.Contain("<order>"));
        });
        File.Delete(html);
    }

    [Test]
    public async Task SchemaWritesEveryPhase()
    {
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"schema-{Guid.NewGuid():N}");
        var (code, _, _) = await Run("schema", "--source", FixturePath, "--out", dir);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.Success));
            Assert.That(Directory.GetFiles(dir, "*.sql"), Has.Length.EqualTo(10));
            Assert.That(File.ReadAllText(Path.Combine(dir, "05-tables.sql")), Does.Contain("CREATE TABLE shop.all_types"));
        });
        Directory.Delete(dir, recursive: true);
    }

    [TestCase(18, "line_tax numeric GENERATED ALWAYS AS (qty * unit_price * 0.1) VIRTUAL")]
    [TestCase(15, "line_tax numeric GENERATED ALWAYS AS (qty * unit_price * 0.1) STORED")]
    public async Task TargetVersionSetsTheOutput(int version, string lineTax)
    {
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"schema-{Guid.NewGuid():N}");
        var (code, _, _) = await Run("schema", "--source", FixturePath, "--out", dir, "--target-version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.Success));
            Assert.That(File.ReadAllText(Path.Combine(dir, "05-tables.sql")), Does.Contain(lineTax));
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task UnsupportedTargetVersionIsAUsageError()
    {
        var (code, _, error) = await Run("assess", "--source", FixturePath, "--target-version", "14");
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.UsageError));
            Assert.That(error, Does.Contain("targetVersion"));
        });
    }

    [Test]
    public async Task UnreachableServerIsAnUnexpectedErrorWithThePasswordMasked()
    {
        var (code, _, error) = await Run("assess", "--source", "Server=127.0.0.1,1;User Id=sa;Password=hunter2;Connect Timeout=1;TrustServerCertificate=true");
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(ExitCodes.UnexpectedError));
            Assert.That(error, Does.Not.Contain("hunter2"));
        });
    }

    [TestCase("Server=a;Password=p@ss;Database=d", "Server=a;Password=***;Database=d")]
    [TestCase("Host=h;pwd = 'x;y';Port=5", "Host=h;pwd=***;Port=5")]
    [TestCase("Host=h;PASSWORD=\"q\"", "Host=h;PASSWORD=***")]
    [TestCase("no secrets here", "no secrets here")]
    public void PasswordsAreMasked(string input, string expected) =>
        Assert.That(Sql2PgCli.Mask(input), Is.EqualTo(expected));

    [Test]
    public void CodesHaveNames() => Assert.That(Sql2PgCli.CodeName("S2P2002"), Is.EqualTo("ClusteredIgnored"));
}
