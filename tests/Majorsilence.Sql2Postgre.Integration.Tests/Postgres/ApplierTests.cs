using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Postgres;

[Category("Containers")]
public class ApplierTests
{
    private static ConvertedObject O(string name, Phase phase, string sql, ObjectStatus status = ObjectStatus.Converted) =>
        new(new ObjectName("dbo", name), ObjectKind.Table, $"public.{name}", phase, sql, status, [], []);

    [Test]
    public async Task FailureIsRecordedAndTheRunContinues()
    {
        await using var db = await PostgresDatabase.CreateAsync();
        var report = await PgApplier.ApplyAsync([
            O("a", Phase.Tables, "CREATE TABLE public.a (id integer);\n"),
            O("broken", Phase.Tables, "CREATE TABLE public.broken (id no_such_type);\n"),
            O("b", Phase.Tables, "CREATE TABLE public.b (id integer);\n"),
        ], db.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(report.Applied, Is.EqualTo(2));
            Assert.That(report.Failed, Is.EqualTo(1));
            var failed = report.Outcomes.Single(o => o.Status == ApplyStatus.Failed);
            Assert.That(failed.TargetName, Is.EqualTo("public.broken"));
            Assert.That(failed.SqlState, Is.EqualTo("42704"), "undefined_object");
        });
    }

    [Test]
    public async Task StopOnErrorSkipsTheRest()
    {
        await using var db = await PostgresDatabase.CreateAsync();
        var report = await PgApplier.ApplyAsync([
            O("broken", Phase.Tables, "CREATE TABLE public.broken (id no_such_type);\n"),
            O("a", Phase.Tables, "CREATE TABLE public.a (id integer);\n"),
        ], db.ConnectionString, new ApplyOptions { StopOnError = true });
        Assert.That(report.Outcomes.Select(o => o.Status), Is.EqualTo(new[] { ApplyStatus.Failed, ApplyStatus.Skipped }));
    }

    [Test]
    public async Task EachObjectIsItsOwnTransaction()
    {
        await using var db = await PostgresDatabase.CreateAsync();
        // The second statement fails, so the first one's table must be rolled back with it.
        await PgApplier.ApplyAsync([O("x", Phase.Tables, "CREATE TABLE public.half (id integer);\nSELECT 1/0;\n")], db.ConnectionString);
        var report = await PgApplier.ApplyAsync([O("half", Phase.Tables, "CREATE TABLE public.half (id integer);\n")], db.ConnectionString);
        Assert.That(report.Applied, Is.EqualTo(1));
    }

    [Test]
    public async Task ManualCodeIsSkippedButManualTablesApply()
    {
        await using var db = await PostgresDatabase.CreateAsync();
        var objects = new[]
        {
            O("t", Phase.Tables, "CREATE TABLE public.t (id integer\n/* sql2pg:untranslated S2P3001\nx DEFAULT (dbo.f())\n*/\n);\n", ObjectStatus.Manual),
            O("f", Phase.Code, "CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;\n", ObjectStatus.Manual),
            O("s", Phase.Code, "", ObjectStatus.Skipped),
        };
        var report = await PgApplier.ApplyAsync(objects, db.ConnectionString);
        var withManual = await PgApplier.ApplyAsync(objects.Skip(1), db.ConnectionString, new ApplyOptions { ApplyManual = true });
        Assert.Multiple(() =>
        {
            Assert.That(report.Outcomes.Select(o => o.Status), Is.EqualTo(new[] { ApplyStatus.Applied, ApplyStatus.Skipped, ApplyStatus.Skipped }));
            Assert.That(withManual.Outcomes.Select(o => o.Status), Is.EqualTo(new[] { ApplyStatus.Applied, ApplyStatus.Skipped }));
        });
    }

    [Test]
    public async Task ScriptDirectoryApplies()
    {
        await using var db = await PostgresDatabase.CreateAsync();
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "apply-" + Guid.NewGuid().ToString("N"));
        ScriptPlanner.WriteFiles(new ConversionResult([
            O("t", Phase.Tables, "CREATE TABLE public.t (id integer);\n"),
            new ConvertedObject(new ObjectName("dbo", "t_pkey"), ObjectKind.Constraint, "public.t.t_pkey", Phase.Constraints,
                "ALTER TABLE public.t ADD CONSTRAINT t_pkey PRIMARY KEY (id);\n", ObjectStatus.Converted, [], []),
        ], []), dir);
        var report = await PgApplier.ApplyDirectoryAsync(dir, db.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(report.Applied, Is.EqualTo(2));
            Assert.That(report.Outcomes.Select(o => o.Phase), Is.EqualTo(new[] { Phase.Tables, Phase.Constraints }));
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task RecreateEmptiesTheDatabase()
    {
        await using var db = await PostgresDatabase.CreateAsync();
        await PgApplier.ApplyAsync([O("t", Phase.Tables, "CREATE TABLE public.t (id integer);\n")], db.ConnectionString);
        await PgApplier.RecreateDatabaseAsync(db.ConnectionString);
        var again = await PgApplier.ApplyAsync([O("t", Phase.Tables, "CREATE TABLE public.t (id integer);\n")], db.ConnectionString);
        Assert.That(again.Applied, Is.EqualTo(1));
    }
}
