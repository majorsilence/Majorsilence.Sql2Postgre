using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Tests.SqlServer;

public class BatchSplitterTests
{
    [Test]
    public void SplitsOnGoLinesInAnyCase() =>
        Assert.That(BatchSplitter.Split("SELECT 1;\nGO\nSELECT 2;\r\ngo\nSELECT 3;"),
            Is.EqualTo(new[] { "SELECT 1;", "SELECT 2;", "SELECT 3;" }));

    [Test]
    public void GoWithCommentAndSpacesIsASeparator() =>
        Assert.That(BatchSplitter.Split("SELECT 1;\n  GO  -- end\nSELECT 2;"), Has.Count.EqualTo(2));

    [Test]
    public void GoInsideALineIsNotASeparator() =>
        Assert.That(BatchSplitter.Split("SELECT 'GO' AS go_col;\nSELECT 1 AS [GO];"), Has.Count.EqualTo(1));

    [Test]
    public void EmptyBatchesAreDropped() =>
        Assert.That(BatchSplitter.Split("\nGO\nGO\nSELECT 1;\nGO\n"), Is.EqualTo(new[] { "SELECT 1;" }));
}
