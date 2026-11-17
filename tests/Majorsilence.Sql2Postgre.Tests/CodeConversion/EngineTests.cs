using Majorsilence.Sql2Postgre.Cli;
using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Majorsilence.Sql2Postgre.Tests.CodeConversion;

public class EngineTests
{
    private static CodeEdit E(int start, int length, string text) => new(new TextSpan(start, length), text, "test");

    [Test]
    public void EditsApplyToTheOriginalPositions()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EditEngine.Apply("abcdef", [E(4, 2, "XY"), E(0, 1, "123")]), Is.EqualTo("123bcdXY"));
            Assert.That(EditEngine.Apply("abc", []), Is.EqualTo("abc"));
            // Touching edits and two inserts at one point (kept in the order given).
            Assert.That(EditEngine.Apply("abcd", [E(0, 2, "X"), E(2, 2, "Y")]), Is.EqualTo("XY"));
            Assert.That(EditEngine.Apply("ab", [E(1, 0, "1"), E(1, 0, "2")]), Is.EqualTo("a12b"));
        });
    }

    [Test]
    public void OverlappingEditsAreRefused()
    {
        Assert.Throws<OverlappingEditsException>(() => EditEngine.Apply("abcdef", [E(0, 3, "X"), E(2, 2, "Y")]));
        Assert.Throws<OverlappingEditsException>(() => EditEngine.Apply("abcdef", [E(1, 2, "X"), E(1, 2, "Y")]));
    }

    [TestCase("plain")]
    [TestCase("with \"quotes\" and \\ backslash")]
    [TestCase("two\nlines")]
    [TestCase("tab\tand \u0001 control")]
    public void CSharpLiteralsRoundTrip(string value)
    {
        string literal = CodeLanguages.CSharp.StringLiteral(value);
        var tree = CodeLanguages.CSharp.Parse($"class C {{ string s = {literal}; }}", "c.cs");
        var node = tree.GetRoot().DescendantNodes().First(n => CodeLanguages.CSharp.StringValue(n) is not null);
        Assert.That(CodeLanguages.CSharp.StringValue(node), Is.EqualTo(value), literal);
    }

    [TestCase("plain")]
    [TestCase("with \"quotes\"")]
    [TestCase("two\nlines")]
    public void VisualBasicLiteralsRoundTrip(string value)
    {
        string literal = CodeLanguages.VisualBasic.StringLiteral(value);
        var tree = CodeLanguages.VisualBasic.Parse($"Module M\n    Dim s As String = {literal}\nEnd Module\n", "m.vb");
        var node = tree.GetRoot().DescendantNodes().First(n => CodeLanguages.VisualBasic.StringValue(n) is not null);
        Assert.That(CodeLanguages.VisualBasic.StringValue(node), Is.EqualTo(value), literal);
    }

    [Test]
    public void CSharpLiteralsKeepTheirForm()
    {
        string source = "class C {\n    string a = @\"x\";\n    string b = \"\"\"\n        SELECT 1\n        \"\"\";\n    string c = \"\"\"one line\"\"\";\n}";
        var literals = CodeLanguages.CSharp.Parse(source, "c.cs").GetRoot().DescendantNodes()
            .Where(n => CodeLanguages.CSharp.StringValue(n) is not null).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(CodeLanguages.CSharp.StringLiteral("say \"hi\"", literals[0]), Is.EqualTo("@\"say \"\"hi\"\"\""));
            Assert.That(CodeLanguages.CSharp.StringLiteral("SELECT 2\nFROM t", literals[1]), Is.EqualTo("\"\"\"\n        SELECT 2\n        FROM t\n        \"\"\""));
            Assert.That(CodeLanguages.CSharp.StringLiteral("two", literals[2]), Is.EqualTo("\"\"\"two\"\"\""));
            Assert.That(CodeLanguages.CSharp.StringLiteral("has \"\"\" inside", literals[2]), Is.EqualTo("\"\"\"\"has \"\"\" inside\"\"\"\""));
        });
    }

    [Test]
    public void ImportsAreRead()
    {
        var cs = CodeLanguages.CSharp.Parse("using System;\nusing Microsoft.Data.SqlClient;\nusing X = System.Text;\nclass C {}", "c.cs");
        var vb = CodeLanguages.VisualBasic.Parse("Imports System.Data.SqlClient\nModule M\nEnd Module\n", "m.vb");
        Assert.Multiple(() =>
        {
            Assert.That(CodeLanguages.CSharp.Imports(cs.GetRoot()), Is.EqualTo(new[] { "System", "Microsoft.Data.SqlClient" }));
            Assert.That(CodeLanguages.VisualBasic.Imports(vb.GetRoot()), Is.EqualTo(new[] { "System.Data.SqlClient" }));
        });
    }

    [Test]
    public void AFileNoRuleTouchesComesBackByteForByte()
    {
        string text = "// comment\r\nusing System;\r\n\r\nclass C\r\n{\r\n    void M() { }   \r\n}\r\n";
        var result = new CodeConverter(new CodeConversionOptions(new ConversionOptions())).ConvertFile("C.cs", text);
        Assert.Multiple(() =>
        {
            Assert.That(result.NewText, Is.EqualTo(text));
            Assert.That(result.Changed, Is.False);
            Assert.That(result.Status, Is.EqualTo(ObjectStatus.Converted));
        });
    }

    [Test]
    public void OverlappingRulesLeaveTheFileAndReportABug()
    {
        var rule = new Overlapping();
        var result = new CodeConverter(new CodeConversionOptions(new ConversionOptions()), [rule]).ConvertFile("C.cs", "class C { }");
        Assert.Multiple(() =>
        {
            Assert.That(result.NewText, Is.EqualTo("class C { }"));
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.InternalError));
        });
    }

    private sealed class Overlapping : ICodeRule
    {
        public void Apply(CodeContext context)
        {
            context.Edit(new TextSpan(0, 5), "x", "one");
            context.Edit(new TextSpan(2, 5), "y", "two");
        }
    }

    [Test]
    public void InputsAreFoundFromAFolderAProjectAndASolution()
    {
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"code-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "App", "obj"));
        Directory.CreateDirectory(Path.Combine(dir, "Lib"));
        File.WriteAllText(Path.Combine(dir, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(dir, "App", "Program.cs"), "class P {}");
        File.WriteAllText(Path.Combine(dir, "App", "obj", "Generated.cs"), "class G {}");
        File.WriteAllText(Path.Combine(dir, "Lib", "Lib.vbproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(dir, "Lib", "Data.vb"), "Module M\nEnd Module\n");
        File.WriteAllText(Path.Combine(dir, "All.slnx"), "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Lib\\Lib.vbproj\" /></Solution>");
        File.WriteAllText(Path.Combine(dir, "All.sln"),
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{1}\"\nEndProject\n");
        try
        {
            string Rel(CodeFile f) => f.RelativePath.Replace('\\', '/');
            Assert.Multiple(() =>
            {
                Assert.That(CodeInput.Discover(dir).Files.Select(Rel), Is.EqualTo(new[] { "App/App.csproj", "App/Program.cs", "Lib/Data.vb", "Lib/Lib.vbproj" }));
                Assert.That(CodeInput.Discover(Path.Combine(dir, "App", "App.csproj")).Files.Select(Rel), Is.EqualTo(new[] { "App.csproj", "Program.cs" }));
                Assert.That(CodeInput.Discover(Path.Combine(dir, "All.slnx")).Files.Select(Rel), Is.EqualTo(new[] { "App/App.csproj", "App/Program.cs", "Lib/Data.vb", "Lib/Lib.vbproj" }));
                Assert.That(CodeInput.Discover(Path.Combine(dir, "All.sln")).Files.Select(Rel), Is.EqualTo(new[] { "App/App.csproj", "App/Program.cs" }));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task CodeVerbMirrorsTheTreeAndKeepsTheEncoding()
    {
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"codeverb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "in", "Data"));
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("class Repo {}\n")];
        await File.WriteAllBytesAsync(Path.Combine(dir, "in", "Data", "Repo.cs"), withBom);
        var output = new StringWriter();
        int code = await new Sql2PgCli(output, new StringWriter()).InvokeAsync(["code", Path.Combine(dir, "in"), "--out", Path.Combine(dir, "out")]);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(code, Is.EqualTo(ExitCodes.Success));
                Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "Data", "Repo.cs")), Is.EqualTo(withBom));
                Assert.That(output.ToString(), Does.Contain("1 file(s) read"));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task CodeVerbNeedsOutOrInPlace()
    {
        var error = new StringWriter();
        int code = await new Sql2PgCli(new StringWriter(), error).InvokeAsync(["code", "."]);
        Assert.That(code, Is.EqualTo(ExitCodes.UsageError), error.ToString());
    }
}
