using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Schema;

namespace Majorsilence.Sql2Postgre.Tests.Translation;

public class AgentJobTests
{
    // freqType, freqInterval, subdayType, subdayInterval, relative, recurrence, start, end → cron
    [TestCase(4, 1, 1, 0, 0, 0, 23000, 235959, "30 2 * * *")]
    [TestCase(4, 0, 1, 0, 0, 1, 0, 235959, "0 0 * * *")]
    [TestCase(8, 42, 1, 0, 0, 1, 180000, 235959, "0 18 * * 1,3,5")]
    [TestCase(8, 65, 1, 0, 0, 1, 93000, 235959, "30 9 * * 0,6")]
    [TestCase(4, 1, 4, 15, 0, 0, 0, 235959, "*/15 * * * *")]
    [TestCase(4, 1, 4, 15, 0, 0, 80000, 175959, "*/15 8-17 * * *")]
    [TestCase(4, 1, 8, 2, 0, 0, 0, 235959, "0 */2 * * *")]
    [TestCase(4, 1, 8, 3, 0, 0, 63000, 220000, "30 6-22/3 * * *")]
    [TestCase(16, 1, 1, 0, 0, 1, 0, 235959, "0 0 1 * *")]
    [TestCase(16, 15, 1, 0, 0, 1, 120000, 235959, "0 12 15 * *")]
    public void SchedulesCronCanSay(int type, int interval, int subday, int subdayInterval, int relative, int recurrence, int start, int end, string expected)
    {
        var s = new AgentSchedule("s", true, type, interval, subday, subdayInterval, relative, recurrence, start, end);
        Assert.That(AgentSchedules.Cron(s, out string? problem), Is.EqualTo(expected), problem);
    }

    [TestCase(4, 2, 1, 0, 0, 0, 0, 235959, "every 2 days")]
    [TestCase(8, 2, 1, 0, 0, 2, 0, 235959, "every 2 weeks")]
    [TestCase(16, 1, 1, 0, 0, 3, 0, 235959, "every 3 months")]
    [TestCase(32, 3, 1, 0, 2, 1, 0, 235959, "relative day")]
    [TestCase(1, 0, 1, 0, 0, 0, 0, 235959, "once")]
    [TestCase(64, 0, 0, 0, 0, 0, 0, 235959, "Agent starts")]
    [TestCase(128, 0, 0, 0, 0, 0, 0, 235959, "idle")]
    [TestCase(4, 1, 2, 30, 0, 0, 0, 235959, "30 seconds")]
    [TestCase(4, 1, 4, 7, 0, 0, 0, 235959, "every 7 minutes")]
    [TestCase(4, 1, 8, 5, 0, 0, 0, 235959, "every 5 hours")]
    public void SchedulesCronCannotSayAreReported(int type, int interval, int subday, int subdayInterval, int relative, int recurrence, int start, int end, string reason)
    {
        var s = new AgentSchedule("s", true, type, interval, subday, subdayInterval, relative, recurrence, start, end);
        Assert.Multiple(() =>
        {
            Assert.That(AgentSchedules.Cron(s, out string? problem), Is.Null);
            Assert.That(problem, Does.Contain(reason));
        });
    }

    [Test]
    public void TheJobsStepsBecomeOneProcedure()
    {
        var job = new AgentJob("Nightly [load]", null, true,
        [
            new AgentJobStep(1, "load", "TSQL", "INSERT INTO dbo.Log VALUES (1)\nGO\nINSERT INTO dbo.Log VALUES (2);", "Shop", 3, 0, 2, 0),
            new AgentJobStep(2, "tidy", "TSQL", "DELETE FROM dbo.Log WHERE Id < 0", "Shop", 1, 0, 3, 0),
            new AgentJobStep(3, "copy files", "CmdExec", "robocopy a b", null, 1, 0, 2, 0),
            new AgentJobStep(4, "elsewhere", "TSQL", "SELECT 1", "Other", 1, 0, 2, 0),
            new AgentJobStep(5, "last", "TSQL", "UPDATE dbo.Log SET Id = Id", "shop", 1, 0, 1, 0),
        ], []);
        Assert.That(AgentJobReader.Procedure(job, "Shop"), Is.EqualTo("""
            CREATE PROCEDURE [sql2pg_agent].[Nightly [load]]] AS
            BEGIN
            INSERT INTO dbo.Log VALUES (1)

            INSERT INTO dbo.Log VALUES (2);
            BEGIN TRY
            DELETE FROM dbo.Log WHERE Id < 0;
            END TRY
            BEGIN CATCH
            PRINT N'Step 2 failed; going on to the next step.';
            END CATCH;
            RETURN;
            BEGIN TRY
            UPDATE dbo.Log SET Id = Id;
            END TRY
            BEGIN CATCH
            RETURN;
            END CATCH;
            END;

            """.Replace("\r\n", "\n")));
    }
}
