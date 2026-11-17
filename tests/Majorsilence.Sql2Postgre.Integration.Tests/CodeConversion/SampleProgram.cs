using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using VB = Microsoft.CodeAnalysis.VisualBasic;

namespace Majorsilence.Sql2Postgre.Integration.Tests.CodeConversion;

/// <summary>
/// Compiles a sample program in memory, with the runtime's assemblies, SqlClient, Npgsql and any
/// other assembly the test names, and runs its <c>Sample.Run(connectionString)</c>, which returns
/// what the program printed. The same program runs before conversion on SQL Server and after
/// it on PostgreSQL.
/// </summary>
public static class SampleProgram
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> Runtime = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList());

    public static string Run(string source, string language, string connectionString, params Assembly[] references)
    {
        var refs = Runtime.Value.Concat(new[] { typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly, typeof(Npgsql.NpgsqlConnection).Assembly }
            .Concat(references).Distinct().Select(a => MetadataReference.CreateFromFile(a.Location))).ToList();
        Compilation compilation = language == "VB"
            ? VB.VisualBasicCompilation.Create($"sample_{Guid.NewGuid():N}", [VB.VisualBasicSyntaxTree.ParseText(source)], refs,
                new VB.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optionStrict: VB.OptionStrict.On))
            : CS.CSharpCompilation.Create($"sample_{Guid.NewGuid():N}", [CS.CSharpSyntaxTree.ParseText(source, new CS.CSharpParseOptions(CS.LanguageVersion.Latest))], refs,
                new CS.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success)
            throw new InvalidOperationException("The sample does not compile:\n"
                + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n\n" + source);
        stream.Position = 0;
        var context = new AssemblyLoadContext(null, isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var run = assembly.GetTypes().Single(t => t.Name == "Sample").GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            try
            {
                return (string)run.Invoke(null, [connectionString])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                return "threw " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            }
        }
        finally
        {
            context.Unload();
        }
    }
}
