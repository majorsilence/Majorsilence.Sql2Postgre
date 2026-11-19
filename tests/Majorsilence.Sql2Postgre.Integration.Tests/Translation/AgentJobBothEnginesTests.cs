using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// A SQL Server Agent job read from msdb and converted for pg_cron: the job's procedure runs its
/// T-SQL steps as the job would (a step that fails and goes on, a CmdExec step left out), and its
/// daily schedule is a pg_cron job while its relative-day schedule is reported. SQL Server Agent
/// does not run in the container, so the steps are run on SQL Server one by one, as the job would.
/// </summary>
[Category("Containers")]
public class AgentJobBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.RunLog (Id int IDENTITY NOT NULL PRIMARY KEY, Note nvarchar(40) NOT NULL);
        GO
        """;

    [Test]
    public async Task TheJobRunsAndIsScheduled()
    {
        string pgMaintenance = await Engines.PgCronAsync();
        await using var source = await SqlServerDatabase.CreateAsync(Script, "agent");
        string job = $"sql2pg nightly {Guid.NewGuid():N}"[..30];
        string[] steps = ["INSERT INTO dbo.RunLog (Note) VALUES (N'step 1');", "RAISERROR(N'step 2 fails', 16, 1);", "INSERT INTO dbo.RunLog (Note) VALUES (N'step 4');"];
        await using (var sql = new SqlConnection(source.ConnectionString))
        {
            await sql.OpenAsync();
            string Text(string s) => "N'" + s.Replace("'", "''") + "'";
            string db = Text(source.Name);
            await using var cmd = new SqlCommand($"""
                EXEC msdb.dbo.sp_add_job @job_name = {Text(job)}, @enabled = 1;
                EXEC msdb.dbo.sp_add_jobstep @job_name = {Text(job)}, @step_name = N'log', @subsystem = N'TSQL', @database_name = {db}, @command = {Text(steps[0])}, @on_success_action = 3, @on_fail_action = 2;
                EXEC msdb.dbo.sp_add_jobstep @job_name = {Text(job)}, @step_name = N'fail softly', @subsystem = N'TSQL', @database_name = {db}, @command = {Text(steps[1])}, @on_success_action = 3, @on_fail_action = 3;
                EXEC msdb.dbo.sp_add_jobstep @job_name = {Text(job)}, @step_name = N'files', @subsystem = N'CmdExec', @command = N'dir', @on_success_action = 3, @on_fail_action = 3;
                EXEC msdb.dbo.sp_add_jobstep @job_name = {Text(job)}, @step_name = N'finish', @subsystem = N'TSQL', @database_name = {db}, @command = {Text(steps[2])}, @on_success_action = 1, @on_fail_action = 2;
                EXEC msdb.dbo.sp_add_schedule @schedule_name = {Text(job + " daily")}, @freq_type = 4, @freq_interval = 1, @active_start_time = 23000;
                EXEC msdb.dbo.sp_attach_schedule @job_name = {Text(job)}, @schedule_name = {Text(job + " daily")};
                EXEC msdb.dbo.sp_add_schedule @schedule_name = {Text(job + " tuesday")}, @freq_type = 32, @freq_interval = 3, @freq_relative_interval = 2, @freq_recurrence_factor = 1;
                EXEC msdb.dbo.sp_attach_schedule @job_name = {Text(job)}, @schedule_name = {Text(job + " tuesday")};
                """, sql);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            var read = await SqlServerCatalogReader.ReadAsync(source.ConnectionString, agentJobs: true);
            Assert.That(read.Database.AgentJobs!.Select(j => j.Name), Is.EqualTo(new[] { job }));
            var options = new ConversionOptions { Extensions = new HashSet<string> { PermittedExtensions.PgCron } };
            var schema = SchemaConverter.Convert(read.Database, options);
            var diagnostics = schema.Objects.SelectMany(o => o.Diagnostics).Select(d => $"{d.Code} {d.Severity}").ToList();
            Assert.That(diagnostics, Is.SupersetOf(new[] { "S2P2023 Warning", "S2P2024 Warning", "S2P2025 Info" }));

            // pg_cron runs (and installs) only in cron.database_name, so the target is that database.
            await using (var maintenance = new NpgsqlConnection(pgMaintenance))
            {
                await maintenance.OpenAsync();
                await using var create = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Engines.PgCronDatabase} WITH (FORCE); CREATE DATABASE {Engines.PgCronDatabase};", maintenance);
                await create.ExecuteNonQueryAsync();
            }
            string target = new NpgsqlConnectionStringBuilder(pgMaintenance) { Database = Engines.PgCronDatabase }.ConnectionString;
            var applied = await PgApplier.ApplyAsync(schema.Objects, target);
            Assert.That(applied.Failed, Is.Zero, string.Join("\n", applied.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));

            await using var pg = new NpgsqlConnection(target);
            await pg.OpenAsync();
            var scheduled = new List<string>();
            await using (var cron = new NpgsqlCommand("SELECT jobname || ' | ' || schedule || ' | ' || command FROM cron.job ORDER BY jobname", pg))
            await using (var r = await cron.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    scheduled.Add(r.GetString(0));
            string procedure = schema.Objects.Single(o => o.Kind == ObjectKind.Procedure).TargetName;
            Assert.That(scheduled, Is.EqualTo(new[] { $"{job}: {job} daily | 30 2 * * * | CALL {procedure}()" }));

            await using (var call = new NpgsqlCommand($"CALL {procedure}()", pg))
                await call.ExecuteNonQueryAsync();
            var pgLog = new List<string>();
            await using (var log = new NpgsqlCommand("SELECT note FROM public.runlog ORDER BY id", pg))
            await using (var r = await log.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    pgLog.Add(r.GetString(0));

            // The job on SQL Server: step 1, step 2 fails and the job goes on, the CmdExec step, step 4.
            var sqlLog = new List<string>();
            await using (var sql = new SqlConnection(source.ConnectionString))
            {
                await sql.OpenAsync();
                foreach (string step in steps)
                {
                    try
                    {
                        await using var run = new SqlCommand(step, sql);
                        await run.ExecuteNonQueryAsync();
                    }
                    catch (SqlException)
                    {
                    }
                }
                await using var log = new SqlCommand("SELECT Note FROM dbo.RunLog ORDER BY Id", sql);
                await using var r = await log.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    sqlLog.Add(r.GetString(0));
            }
            Assert.That(pgLog, Is.EqualTo(sqlLog));
            Assert.That(pgLog, Is.EqualTo(new[] { "step 1", "step 4" }));
        }
        finally
        {
            await using var sql = new SqlConnection(source.ConnectionString);
            await sql.OpenAsync();
            await using var drop = new SqlCommand($"EXEC msdb.dbo.sp_delete_job @job_name = N'{job}', @delete_unused_schedule = 1;", sql);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
