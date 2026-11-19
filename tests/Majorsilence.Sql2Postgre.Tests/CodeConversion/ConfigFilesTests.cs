using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Tests.CodeConversion;

public class ConfigFilesTests
{
    [Test]
    public void DiagnosticsNameTheSettingNotItsValue()
    {
        const string json = """
            { "ConnectionStrings": { "Main": "Server=db\\inst;Database=Shop;User Id=sa;Password=Hunter2!;Integrated Security=true;Weird=1" } }
            """;
        const string xml = """<configuration><connectionStrings><add name="Main" connectionString="Server=db;Database=Shop;User Id=sa;Password=Hunter2!;Weird=1" providerName="System.Data.SqlClient"/></connectionStrings></configuration>""";
        var results = new[] { ConfigFiles.Convert("appsettings.json", json), ConfigFiles.Convert("web.config", xml) };
        Assert.Multiple(() =>
        {
            foreach (var r in results)
            {
                Assert.That(r.Changed, Is.True, r.Path);
                Assert.That(r.Diagnostics.Select(d => d.Message), Has.None.Contains("Hunter2"), r.Path);
                Assert.That(r.Diagnostics.Where(d => d.Code == DiagnosticCodes.ConfigConnectionString).Select(d => d.Message), Has.All.Contains("Main"), r.Path);
            }
        });
    }

    [Test]
    public void FolderDiscoveryIncludesConfigurationFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sql2pg-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        try
        {
            foreach (string f in new[] { "appsettings.json", "appsettings.Production.json", "Web.config", "App.config", "Db.sqlproj", "other.json", Path.Combine("bin", "appsettings.json") })
                File.WriteAllText(Path.Combine(dir, f), "{}");
            var (_, files) = CodeInput.Discover(dir);
            Assert.That(files.Select(f => f.RelativePath.Replace('\\', '/')).Order(StringComparer.Ordinal),
                Is.EqualTo(new[] { "App.config", "Db.sqlproj", "Web.config", "appsettings.Production.json", "appsettings.json" }.Order(StringComparer.Ordinal)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
