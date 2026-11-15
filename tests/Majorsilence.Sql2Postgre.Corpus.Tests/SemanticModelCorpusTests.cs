using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Corpus.Tests;

/// <summary>
/// The semantic model on real code: every corpus module parses, and for each database the share
/// of view columns whose type it can tell is recorded (counts only).
/// </summary>
[Category("Containers")]
[Category("Corpus")]
public class SemanticModelCorpusTests
{
    public static IEnumerable<TestCaseData> Databases() =>
        CorpusEntry.All.Select(e => new TestCaseData(e).SetName($"SemanticModel({e.Database})"));

    [TestCaseSource(nameof(Databases))]
    public async Task BuildsOverEveryModule(CorpusEntry entry)
    {
        var db = (await SqlServerCatalogReader.ReadAsync(await CorpusSource.ConnectionStringAsync(entry))).Database;
        var model = SemanticModel.Build(db);

        var views = db.Modules.Where(m => m.Kind == ObjectKind.View).Select(m => model.Relation(m.Name)).ToList();
        int columns = views.Sum(v => v?.Columns.Count ?? 0);
        int typed = views.Sum(v => v?.Columns.Count(c => c.Type is not null) ?? 0);
        int routines = db.Modules.Count(m => m.Kind is ObjectKind.Function or ObjectKind.Procedure && model.Routine(m.Name) is not null);
        TestContext.Out.WriteLine($"{entry.Database}: {db.Modules.Count} modules, {routines} routine signatures, "
                                  + $"{views.Count} views with {typed}/{columns} columns typed, {model.Dependencies.Count} dependencies, "
                                  + $"diagnostics: {string.Join(", ", model.Diagnostics.GroupBy(d => d.Code).Select(g => $"{g.Key}×{g.Count()}"))}");
        foreach (var d in model.Diagnostics.Where(d => d.Code == DiagnosticCodes.ExternalReference))
            TestContext.Out.WriteLine($"  {d.Message}");

        Assert.Multiple(() =>
        {
            Assert.That(model.Diagnostics.Where(d => d.Code == DiagnosticCodes.ParseError).Select(d => d.Message), Is.Empty, "every module parses");
            Assert.That(views.All(v => v is not null), Is.True, "every view resolves");
        });
    }
}
