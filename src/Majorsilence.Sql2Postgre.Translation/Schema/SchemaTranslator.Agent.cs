using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>
/// SQL Server Agent jobs (read with <c>--agent-jobs</c>): each job's procedure is converted with the
/// other procedures; with pg_cron enabled, each of its schedules cron can express becomes
/// <c>cron.schedule(name, expression, 'CALL procedure()')</c>, switched off when the job or the
/// schedule is disabled. pg_cron runs jobs in the database <c>cron.database_name</c> names, so that
/// has to be the target database.
/// </summary>
public sealed partial class SchemaTranslator
{
    private IEnumerable<TranslatedObject> AgentJobs(SourceDatabase db)
    {
        foreach (var job in db.AgentJobs ?? [])
        {
            var diagnostics = new List<Diagnostic>();
            var source = new ObjectName(AgentJob.Schema, job.Name);
            foreach (var step in job.Steps)
            {
                if (!step.IsTsqlIn(db.Name))
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentStepUnconverted, Severity.Warning, source,
                        $"Step {step.Id} ({step.Name}) of job {job.Name} is not converted: it is a {step.Subsystem} step{(step.DatabaseName is { } other && !other.Equals(db.Name, StringComparison.OrdinalIgnoreCase) ? $" in database {other}" : "")}. Run it another way (a pg_cron job of its own, or the operating system's scheduler)."));
                else if (step.OnFailAction == 4 || step.OnSuccessAction == 4)
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentStepUnconverted, Severity.Warning, source,
                        $"Step {step.Id} ({step.Name}) of job {job.Name} goes on to step {(step.OnFailAction == 4 ? step.OnFailStepId : step.OnSuccessStepId)}; the converted procedure runs the steps in order instead."));
            }
            string procedure = _names.Routine(job.Procedure).Qualified;
            string target = $"cron.job {job.Name}";
            if (!_options.AllowsExtension(PermittedExtensions.PgCron))
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentScheduleUnmapped, Severity.Info, source,
                    $"pg_cron is not enabled: job {job.Name} is converted as the procedure {procedure}, and not scheduled."));
                yield return new TranslatedObject(source, ObjectKind.Job, target, Phase.Triggers, [], diagnostics, [job.Procedure]) { Skipped = true };
                continue;
            }
            var statements = new List<PgStatement>();
            foreach (var schedule in job.Schedules)
            {
                if (AgentSchedules.Cron(schedule, out string? problem) is not { } cron)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentScheduleUnmapped, Severity.Warning, source,
                        $"Schedule {schedule.Name} of job {job.Name} is not converted: {problem}. Schedule {procedure} by hand."));
                    continue;
                }
                string name = job.Schedules.Count == 1 ? job.Name : $"{job.Name}: {schedule.Name}";
                statements.Add(new PgQueryStatement(new PgSelect
                {
                    Targets = [new PgTarget(new PgFunctionCall("cron.schedule",
                        [PgLiteral.String(name), PgLiteral.String(cron), PgLiteral.String($"CALL {procedure}()")]))],
                }));
                bool active = job.Enabled && schedule.Enabled;
                if (!active)
                    statements.Add(new PgQueryStatement(new PgSelect
                    {
                        Targets = [new PgTarget(new PgTemplate("cron.alter_job(jobid, active => false)", []))],
                        From = [new PgTableRef(new PgName("cron", "job"))],
                        Where = new PgBinary("=", new PgColumnRef("jobname"), PgLiteral.String(name)),
                    }));
                diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentJobScheduled, Severity.Info, source,
                    $"Job {job.Name}{(job.Schedules.Count == 1 ? "" : $" ({schedule.Name})")} is scheduled with pg_cron at '{cron}' (UTC unless cron.timezone says otherwise){(active ? "" : ", switched off as in SQL Server")}. pg_cron runs it in the database cron.database_name names: set that to this database."));
            }
            if (job.Schedules.Count == 0)
                diagnostics.Add(new Diagnostic(DiagnosticCodes.AgentScheduleUnmapped, Severity.Info, source,
                    $"Job {job.Name} has no schedule: it is converted as the procedure {procedure}, and not scheduled."));
            yield return new TranslatedObject(source, ObjectKind.Job, target, Phase.Triggers, statements, diagnostics, [job.Procedure])
            {
                Extensions = statements.Count > 0 ? [PermittedExtensions.PgCron] : [],
                Skipped = statements.Count == 0,
            };
        }
    }
}
