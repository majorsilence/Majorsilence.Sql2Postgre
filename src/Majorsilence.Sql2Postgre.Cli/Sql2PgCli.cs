using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Cli;

/// <summary>Exit codes: what a script calling <c>sql2pg</c> can rely on.</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The command ran, but some objects need a person, failed to apply, or did not verify.</summary>
    public const int CompletedWithProblems = 1;

    public const int UsageError = 2;
    public const int UnexpectedError = 3;
}

/// <summary>An error in what the user asked for (exit code 2), as opposed to a failure while doing it.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The <c>sql2pg</c> command line: <c>assess</c>, <c>schema</c>, <c>apply</c>, <c>data</c> and
/// <c>migrate</c>. Connection strings come from options or the <c>SQL2PG_SOURCE</c> /
/// <c>SQL2PG_TARGET</c> environment variables and are never printed unmasked.
/// </summary>
public sealed partial class Sql2PgCli(TextWriter output, TextWriter error)
{
    public const string SourceVariable = "SQL2PG_SOURCE";
    public const string TargetVariable = "SQL2PG_TARGET";

    private readonly Option<string?> _source = new("--source", "-s")
    {
        Description = $"SQL Server connection string (anything containing '='), or a folder or file of CREATE scripts. Default: ${SourceVariable}.",
    };
    private readonly Option<string?> _target = new("--target", "-t")
    {
        Description = $"PostgreSQL connection string. Default: ${TargetVariable}.",
    };
    private readonly Option<FileInfo?> _config = new("--config", "-c") { Description = "Conversion options as JSON (sql2pg.json)." };
    private readonly Option<FileInfo?> _json = new("--json") { Description = "Write the full report as JSON to this file." };
    private readonly Option<FileInfo?> _html = new("--html") { Description = "Write the report as a single self-contained HTML file (summary, effort, sortable object table)." };
    private readonly Option<DirectoryInfo> _out = new("--out", "-o") { Description = "Directory for the phase scripts and report.json.", Required = true };
    private readonly Option<DirectoryInfo> _scripts = new("--scripts") { Description = "Directory written by 'sql2pg schema'.", Required = true };
    private readonly Option<bool> _stopOnError = new("--stop-on-error") { Description = "Stop at the first object that fails to apply." };
    private readonly Option<bool> _applyManual = new("--apply-manual") { Description = "Also apply views, routines and triggers marked Manual." };
    private readonly Option<bool> _recreate = new("--recreate") { Description = "DROP and re-CREATE the target database first. Destroys everything in it." };
    private readonly Option<string?> _tables = new("--tables") { Description = "Only these tables, comma-separated (schema.table or table)." };
    private readonly Option<int> _parallel = new("--parallel") { Description = "Tables copied at once.", DefaultValueFactory = _ => 4 };
    private readonly Option<FileInfo?> _state = new("--state") { Description = "Resume file: finished tables are recorded here and skipped on a rerun." };
    private readonly Option<string?> _verify = new("--verify") { Description = "Verify copied data: 'count' or 'checksum'." };
    private readonly Option<DirectoryInfo?> _translateOut = new("--out", "-o") { Description = "Where to write the .pgsql files (default: next to each .sql)." };

    public static Task<int> RunAsync(string[] args) => new Sql2PgCli(Console.Out, Console.Error).InvokeAsync(args);

    public async Task<int> InvokeAsync(string[] args, CancellationToken ct = default)
    {
        var root = Build();
        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors)
                await error.WriteLineAsync($"error: {e.Message}");
            await error.WriteLineAsync("Run 'sql2pg --help' for usage.");
            return ExitCodes.UsageError;
        }
        return await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);
    }

    public RootCommand Build()
    {
        var root = new RootCommand("sql2pg: convert a SQL Server database (schema, code and data) to PostgreSQL.");

        var assess = new Command("assess", "Convert without writing anything and report what converts, with warnings, or needs a person.")
            { _source, _config, _json, _html };
        assess.SetAction((p, ct) => Guarded(() => AssessAsync(p, ct)));

        var schema = new Command("schema", "Write the PostgreSQL schema as one script per phase, plus report.json.")
            { _source, _out, _config };
        schema.SetAction((p, ct) => Guarded(() => SchemaAsync(p, ct)));

        var apply = new Command("apply", "Run the scripts 'sql2pg schema' wrote against a PostgreSQL database.")
            { _scripts, _target, _stopOnError, _applyManual, _recreate };
        apply.SetAction((p, ct) => Guarded(() => ApplyAsync(p, ct)));

        var data = new Command("data", "Copy table data from SQL Server to PostgreSQL (the tables must exist).")
            { _source, _target, _config, _tables, _parallel, _state, _verify };
        data.SetAction((p, ct) => Guarded(() => DataAsync(p, ct)));

        var migrate = new Command("migrate", "Schema, data, then constraints, indexes and code, then verification.")
            { _source, _target, _config, _parallel, _state, _verify, _stopOnError, _applyManual, _recreate };
        migrate.SetAction((p, ct) => Guarded(() => MigrateAsync(p, ct)));

        var path = new Argument<string>("path") { Description = "A .sql file, or a folder of them (searched recursively)." };
        var translate = new Command("translate", "Translate T-SQL scripts (queries and DML) to PostgreSQL, one .pgsql per .sql.")
            { path, _translateOut, _source, _config };
        translate.SetAction((p, ct) => Guarded(() => TranslateAsync(p, p.GetValue(path)!, ct)));

        root.Subcommands.Add(translate);
        root.Subcommands.Add(assess);
        root.Subcommands.Add(schema);
        root.Subcommands.Add(apply);
        root.Subcommands.Add(data);
        root.Subcommands.Add(migrate);
        root.Subcommands.Add(CodeCommand());
        return root;
    }

    private async Task<int> Guarded(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync($"error: {Mask(ex.Message)}");
            return ExitCodes.UsageError;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled.");
            return ExitCodes.UnexpectedError;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"error: {ex.GetType().Name}: {Mask(ex.Message)}");
            return ExitCodes.UnexpectedError;
        }
    }

    // ---- commands ----

    /// <summary>
    /// Each .sql becomes .pgsql and .diag.json, next to it or mirrored under --out. --source gives
    /// the database's schema for names and types; without it the conversions that need types are not made.
    /// </summary>
    private async Task<int> TranslateAsync(ParseResult p, string path, CancellationToken ct)
    {
        var options = Options(p);
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : File.Exists(path) ? [path] : throw new UsageException($"'{path}' is not a file or a folder.");
        string root = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path))!;
        var model = Translation.Semantics.SemanticModel.Build(p.GetValue(_source) is { } source
            ? (await ReadSourceAsync(source, ct)).Database
            : new SourceDatabase("", "", 160, [], [], [], [], [], []));
        if (p.GetValue(_source) is null)
            await output.WriteLineAsync("No --source: column types are unknown, so conversions that depend on them are not made.");

        bool errors = false;
        foreach (string file in files)
        {
            var result = ScriptTranslator.Translate(await File.ReadAllTextAsync(file, ct), model, options, Path.GetFileName(file));
            string target = p.GetValue(_translateOut) is { } outDir
                ? Path.Combine(outDir.FullName, Path.GetRelativePath(root, file))
                : file;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(Path.ChangeExtension(target, ".pgsql"), result.Sql, ct);
            await File.WriteAllTextAsync(Path.ChangeExtension(target, ".diag.json"), JsonSerializer.Serialize(result.Diagnostics, ConversionOptions.Json), ct);
            int e = result.Diagnostics.Count(d => d.Severity == Severity.Error), w = result.Diagnostics.Count(d => d.Severity == Severity.Warning);
            errors |= e > 0;
            await output.WriteLineAsync($"  {Path.GetRelativePath(root, file)}: {e} error(s), {w} warning(s)");
        }
        return errors ? ExitCodes.CompletedWithProblems : ExitCodes.Success;
    }

    private async Task<int> AssessAsync(ParseResult p, CancellationToken ct)
    {
        var options = Options(p);
        var read = await ReadSourceAsync(Required(p.GetValue(_source), SourceVariable, "--source"), ct);
        var result = SchemaConverter.Convert(read.Database, options);
        await WriteSummaryAsync(read, result);
        if (p.GetValue(_json) is { } json)
            await File.WriteAllTextAsync(json.FullName, JsonSerializer.Serialize(new
            {
                readerDiagnostics = read.Diagnostics,
                effort = DiagnosticCodes.Effort(read.Diagnostics.Concat(result.Diagnostics).Concat(result.Objects.SelectMany(o => o.Diagnostics))),
                objectEffort = result.Objects.Where(o => o.Diagnostics.Count > 0)
                    .Select(o => new { source = o.Source.ToString(), kind = o.Kind, status = o.Status, effort = DiagnosticCodes.Effort(o.Diagnostics) }),
                conversion = result,
            }, ConversionOptions.Json), ct);
        if (p.GetValue(_html) is { } html)
        {
            await File.WriteAllTextAsync(html.FullName, HtmlReport.Render(read.Database.Name, options.TargetVersion, read, result, DateTimeOffset.UtcNow), ct);
            await output.WriteLineAsync($"HTML report written to {html.FullName}");
        }
        return HasManual(read, result) ? ExitCodes.CompletedWithProblems : ExitCodes.Success;
    }

    private async Task<int> SchemaAsync(ParseResult p, CancellationToken ct)
    {
        var options = Options(p);
        var read = await ReadSourceAsync(Required(p.GetValue(_source), SourceVariable, "--source"), ct);
        var result = SchemaConverter.Convert(read.Database, options);
        var dir = p.GetValue(_out)!;
        ScriptPlanner.WriteFiles(result, dir.FullName);
        await WriteSummaryAsync(read, result);
        await output.WriteLineAsync($"Scripts written to {dir.FullName}");
        return HasManual(read, result) ? ExitCodes.CompletedWithProblems : ExitCodes.Success;
    }

    private async Task<int> ApplyAsync(ParseResult p, CancellationToken ct)
    {
        string target = Required(p.GetValue(_target), TargetVariable, "--target");
        var dir = p.GetValue(_scripts)!;
        if (!dir.Exists)
            throw new UsageException($"Scripts directory '{dir.FullName}' does not exist.");
        if (p.GetValue(_recreate))
            await PgApplier.RecreateDatabaseAsync(target, ct);
        var report = await PgApplier.ApplyDirectoryAsync(dir.FullName, target, ApplyOptionsFrom(p), ct);
        await WriteApplyAsync(report);
        return report.Failed > 0 ? ExitCodes.CompletedWithProblems : ExitCodes.Success;
    }

    private async Task<int> DataAsync(ParseResult p, CancellationToken ct)
    {
        var options = Options(p);
        string source = Required(p.GetValue(_source), SourceVariable, "--source");
        string target = Required(p.GetValue(_target), TargetVariable, "--target");
        var verify = VerifyModeFrom(p);
        var read = await ReadLiveSourceAsync(source, ct);
        var copy = await DataCopier.CopyAsync(read.Database, options, source, target, CopyOptionsFrom(p), progress: null, ct);
        await WriteCopyAsync(copy);
        bool ok = copy.Succeeded;
        if (verify is { } mode)
            ok &= await VerifyAsync(read.Database, options, source, target, mode, Tables(p), ct);
        return ok ? ExitCodes.Success : ExitCodes.CompletedWithProblems;
    }

    /// <summary>
    /// Everything in the order that loads fastest and fails least: the schema up to the tables,
    /// the data, then compat helpers, code, indexes, constraints, foreign keys and triggers, then
    /// verification (counts unless <c>--verify checksum</c>).
    /// </summary>
    private async Task<int> MigrateAsync(ParseResult p, CancellationToken ct)
    {
        var options = Options(p);
        string source = Required(p.GetValue(_source), SourceVariable, "--source");
        string target = Required(p.GetValue(_target), TargetVariable, "--target");
        var verify = VerifyModeFrom(p) ?? VerifyMode.Count;
        var read = await ReadLiveSourceAsync(source, ct);
        var result = SchemaConverter.Convert(read.Database, options);
        await WriteSummaryAsync(read, result);

        if (p.GetValue(_recreate))
            await PgApplier.RecreateDatabaseAsync(target, ct);
        var applyOptions = ApplyOptionsFrom(p);
        await output.WriteLineAsync("Creating schemas, types, sequences and tables…");
        var before = await PgApplier.ApplyAsync(result.Objects.Where(o => o.Phase <= Phase.Tables), target, applyOptions, ct);
        await WriteApplyAsync(before);
        if (before.Failed > 0 && applyOptions.StopOnError)
            return ExitCodes.CompletedWithProblems;

        await output.WriteLineAsync("Copying data…");
        var copy = await DataCopier.CopyAsync(read.Database, options, source, target, CopyOptionsFrom(p), progress: null, ct);
        await WriteCopyAsync(copy);

        await output.WriteLineAsync("Creating code, indexes, constraints, foreign keys and triggers…");
        var after = await PgApplier.ApplyAsync(result.Objects.Where(o => o.Phase > Phase.Tables), target, applyOptions, ct);
        await WriteApplyAsync(after);

        bool verified = await VerifyAsync(read.Database, options, source, target, verify, [], ct);
        bool ok = before.Failed == 0 && after.Failed == 0 && copy.Succeeded && verified && !HasManual(read, result);
        return ok ? ExitCodes.Success : ExitCodes.CompletedWithProblems;
    }

    private async Task<bool> VerifyAsync(SourceDatabase db, ConversionOptions options, string source, string target,
        VerifyMode mode, IReadOnlyList<string> tables, CancellationToken ct)
    {
        await output.WriteLineAsync($"Verifying ({mode.ToString().ToLowerInvariant()})…");
        var result = await DataVerifier.VerifyAsync(db, options, source, target, mode, tables, ct: ct);
        foreach (var t in result.Tables.Where(t => !t.Passed))
            foreach (var d in t.Diagnostics)
                await output.WriteLineAsync($"  {d}");
        await output.WriteLineAsync(result.Passed
            ? $"  All {result.Tables.Count} tables verified."
            : $"  {result.Tables.Count(t => !t.Passed)} of {result.Tables.Count} tables did not verify.");
        return result.Passed;
    }

    // ---- inputs ----

    private static string Required(string? value, string variable, string option) =>
        !string.IsNullOrWhiteSpace(value) ? value
        : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv ? fromEnv
        : throw new UsageException($"{option} is required (or set {variable}).");

    private ConversionOptions Options(ParseResult p)
    {
        var options = new ConversionOptions();
        if (p.GetValue(_config) is { } file)
        {
            if (!file.Exists)
                throw new UsageException($"Config file '{file.FullName}' does not exist.");
            try
            {
                options = ConversionOptions.FromJson(File.ReadAllText(file.FullName));
            }
            catch (JsonException ex)
            {
                throw new UsageException($"Config file '{file.FullName}' is not valid: {ex.Message}");
            }
        }
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new UsageException(string.Join(" ", errors));
        return options;
    }

    public static bool IsConnectionString(string source) => source.Contains('=');

    private static async Task<SourceReadResult> ReadSourceAsync(string source, CancellationToken ct)
    {
        if (IsConnectionString(source))
            return await SqlServerCatalogReader.ReadAsync(source, ct);
        if (Directory.Exists(source))
            return SqlServerScriptReader.ReadDirectory(source);
        if (File.Exists(source))
            return SqlServerScriptReader.Read([(source, await File.ReadAllTextAsync(source, ct))], Path.GetFileNameWithoutExtension(source));
        throw new UsageException($"--source '{source}' is not a connection string, a folder or a file.");
    }

    private static Task<SourceReadResult> ReadLiveSourceAsync(string source, CancellationToken ct) =>
        IsConnectionString(source)
            ? SqlServerCatalogReader.ReadAsync(source, ct)
            : throw new UsageException("Copying data needs a live SQL Server: --source must be a connection string.");

    private IReadOnlyList<string> Tables(ParseResult p) =>
        p.GetValue(_tables)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private DataCopyOptions CopyOptionsFrom(ParseResult p)
    {
        int parallel = p.GetValue(_parallel);
        if (parallel < 1)
            throw new UsageException("--parallel must be at least 1.");
        return new DataCopyOptions { MaxParallelTables = parallel, StatePath = p.GetValue(_state)?.FullName, Tables = Tables(p) };
    }

    private ApplyOptions ApplyOptionsFrom(ParseResult p) =>
        new() { StopOnError = p.GetValue(_stopOnError), ApplyManual = p.GetValue(_applyManual) };

    private VerifyMode? VerifyModeFrom(ParseResult p) => p.GetValue(_verify)?.ToLowerInvariant() switch
    {
        null => null,
        "count" => VerifyMode.Count,
        "checksum" => VerifyMode.Checksum,
        var other => throw new UsageException($"--verify must be 'count' or 'checksum', not '{other}'."),
    };

    // ---- output ----

    private static bool HasManual(SourceReadResult read, ConversionResult result) =>
        read.Diagnostics.Any(d => d.Severity == Severity.Error) || result.Objects.Any(o => o.Status == ObjectStatus.Manual);

    private async Task WriteSummaryAsync(SourceReadResult read, ConversionResult result)
    {
        var statuses = Enum.GetValues<ObjectStatus>();
        await output.WriteLineAsync($"{"Kind",-12}{string.Concat(statuses.Select(s => $"{s,24}"))}");
        foreach (var kind in result.Objects.Select(o => o.Kind).Distinct().Order())
            await output.WriteLineAsync($"{kind,-12}{string.Concat(statuses.Select(s => $"{result.Objects.Count(o => o.Kind == kind && o.Status == s),24}"))}");

        var all = read.Diagnostics.Concat(result.Diagnostics).Concat(result.Objects.SelectMany(o => o.Diagnostics)).ToList();
        if (all.Count == 0)
            return;
        await output.WriteLineAsync();
        await output.WriteLineAsync($"Estimated effort: {DiagnosticCodes.Effort(all)} points (Info 0, Warning 1, Error 5; heavier for code to rewrite by hand).");
        var hardest = result.Objects.Select(o => (Object: o, Effort: DiagnosticCodes.Effort(o.Diagnostics))).Where(x => x.Effort > 0)
            .OrderByDescending(x => x.Effort).ThenBy(x => x.Object.TargetName, StringComparer.Ordinal).Take(10).ToList();
        if (hardest.Count > 0)
        {
            await output.WriteLineAsync("Objects needing the most work:");
            foreach (var (o, effort) in hardest)
                await output.WriteLineAsync($"  {effort,5}  {o.Kind,-10} {o.Source}  ({string.Join(", ", o.Diagnostics.Where(d => d.Severity != Severity.Info).Select(d => d.Code).Distinct())})");
        }
        await output.WriteLineAsync();
        await output.WriteLineAsync("Most frequent diagnostics (see docs/diagnostics.md):");
        foreach (var g in all.GroupBy(d => (d.Code, d.Severity)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Code, StringComparer.Ordinal).Take(20))
            await output.WriteLineAsync($"  {g.Key.Code} {g.Key.Severity,-8}{g.Count(),6}  {CodeName(g.Key.Code)}");
        foreach (var d in read.Diagnostics.Where(d => d.Severity == Severity.Error))
            await output.WriteLineAsync($"  {d}");
    }

    private async Task WriteApplyAsync(ApplyReport report)
    {
        await output.WriteLineAsync($"  Applied {report.Applied}, failed {report.Failed}, skipped {report.Skipped}.");
        foreach (var f in report.Outcomes.Where(o => o.Status == ApplyStatus.Failed))
            await output.WriteLineAsync($"  FAILED {f.Kind} {f.TargetName}: {f.SqlState} {f.Message}");
    }

    private async Task WriteCopyAsync(DataCopyResult copy)
    {
        foreach (var t in copy.Tables)
            await output.WriteLineAsync($"  {t.Table,-40} {t.Rows.ToString("N0", CultureInfo.InvariantCulture),15} rows{(t.Error is null ? "" : "  FAILED: " + Mask(t.Error))}");
        foreach (var d in copy.Diagnostics.Where(d => d.Severity == Severity.Warning))
            await output.WriteLineAsync($"  {d}");
    }

    private static readonly Dictionary<string, string> CodeNames = typeof(DiagnosticCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name);

    public static string CodeName(string code) => CodeNames.GetValueOrDefault(code, "");

    /// <summary>Hides passwords in anything that might contain a connection string.</summary>
    public static string Mask(string text) => PasswordPart().Replace(text, m => $"{m.Groups[1].Value}=***");

    [GeneratedRegex(@"\b(Password|Pwd)\s*=\s*('[^']*'|""[^""]*""|[^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPart();
}
