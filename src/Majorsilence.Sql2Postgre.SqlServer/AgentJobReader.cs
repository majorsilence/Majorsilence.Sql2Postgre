using System.Text;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Microsoft.Data.SqlClient;

namespace Majorsilence.Sql2Postgre.SqlServer;

/// <summary>
/// SQL Server Agent jobs from msdb: the jobs with a step in the database being read, their steps
/// and schedules. Each job becomes a module: a procedure in <see cref="AgentJob.Schema"/> whose
/// body runs its T-SQL steps in order, with each step's failure and success actions as far as one
/// procedure can follow them (quit with success is RETURN, quit with failure re-raises, going on
/// to the next step catches the error). Going to another step by number is not kept.
/// </summary>
public static partial class AgentJobReader
{
    /// <summary>A step's command without its GO lines: the step runs as one batch in the procedure.</summary>
    private static string Batch(string command) => GoLine().Replace(command, "").TrimEnd().TrimEnd(';');

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    public static async Task<(IReadOnlyList<AgentJob> Jobs, IReadOnlyList<Module> Modules, IReadOnlyList<Diagnostic> Diagnostics)> ReadAsync(
        SqlConnection conn, string database, CancellationToken ct)
    {
        var diagnostics = new List<Diagnostic>();
        var jobs = new List<(Guid Id, string Name, string? Description, bool Enabled)>();
        var steps = new List<(Guid Job, AgentJobStep Step)>();
        var schedules = new List<(Guid Job, AgentSchedule Schedule)>();
        try
        {
            await using (var cmd = new SqlCommand("""
                SELECT j.job_id, j.name, j.description, j.enabled FROM msdb.dbo.sysjobs AS j
                WHERE EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps AS s WHERE s.job_id = j.job_id AND s.database_name = DB_NAME())
                ORDER BY j.name
                """, conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    jobs.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetByte(3) == 1));
            await using (var cmd = new SqlCommand("""
                SELECT job_id, step_id, step_name, subsystem, command, database_name, on_success_action, on_success_step_id, on_fail_action, on_fail_step_id
                FROM msdb.dbo.sysjobsteps ORDER BY job_id, step_id
                """, conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    steps.Add((r.GetGuid(0), new AgentJobStep(r.GetInt32(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4),
                        r.IsDBNull(5) ? null : r.GetString(5), r.GetByte(6), r.GetInt32(7), r.GetByte(8), r.GetInt32(9))));
            await using (var cmd = new SqlCommand("""
                SELECT js.job_id, s.name, s.enabled, s.freq_type, s.freq_interval, s.freq_subday_type, s.freq_subday_interval,
                       s.freq_relative_interval, s.freq_recurrence_factor, s.active_start_time, s.active_end_time
                FROM msdb.dbo.sysjobschedules AS js JOIN msdb.dbo.sysschedules AS s ON s.schedule_id = js.schedule_id
                ORDER BY js.job_id, s.name
                """, conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    schedules.Add((r.GetGuid(0), new AgentSchedule(r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
                        r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10))));
        }
        catch (SqlException ex)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentStepUnconverted, Severity.Warning, null,
                $"SQL Server Agent jobs could not be read from msdb ({ex.Message}); none are converted. Reading them needs SELECT on msdb's sysjobs, sysjobsteps, sysjobschedules and sysschedules."));
            return ([], [], diagnostics);
        }

        var result = jobs.Select(j => new AgentJob(j.Name, j.Description, j.Enabled,
            steps.Where(s => s.Job == j.Id).Select(s => s.Step).ToList(),
            schedules.Where(s => s.Job == j.Id).Select(s => s.Schedule).ToList())).ToList();
        var modules = result.Select(job => new Module(job.Procedure, ObjectKind.Procedure, Procedure(job, database))).ToList();
        return (result, modules, diagnostics);
    }

    /// <summary>The job's T-SQL steps as one procedure.</summary>
    public static string Procedure(AgentJob job, string database)
    {
        static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
        var sb = new StringBuilder();
        sb.Append($"CREATE PROCEDURE {Quote(AgentJob.Schema)}.{Quote(job.Name)} AS\nBEGIN\n");
        var converted = job.Steps.Where(s => s.IsTsqlIn(database)).ToList();
        foreach (var step in converted)
        {
            bool last = ReferenceEquals(step, converted[^1]);
            // Quit with failure (the default) needs nothing: the error ends the procedure.
            if (step.OnFailAction == 2)
                sb.Append(Batch(step.Command)).Append(";\n");
            else
            {
                sb.Append("BEGIN TRY\n").Append(Batch(step.Command)).Append(";\nEND TRY\nBEGIN CATCH\n");
                sb.Append(step.OnFailAction == 1 ? "RETURN;\n" : "PRINT N'Step " + step.Id + " failed; going on to the next step.';\n");
                sb.Append("END CATCH;\n");
            }
            if (!last && step.OnSuccessAction == 1)
                sb.Append("RETURN;\n");
            else if (step.OnSuccessAction == 2)
                sb.Append($"THROW 50000, N'{job.Name.Replace("'", "''")}: step {step.Id} ends the job reporting failure.', 1;\n");
        }
        if (converted.Count == 0)
            sb.Append("RETURN;\n");
        sb.Append("END;\n");
        return sb.ToString();
    }
}
