using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Expressions;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Types;

namespace Majorsilence.Sql2Postgre.Tests.Golden;

public sealed record GoldenDiagnostic(string Code, string Severity);

public sealed record GoldenResult(string Sql, IReadOnlyList<GoldenDiagnostic> Diagnostics);

/// <summary>
/// Runs a golden case through the translator its area names. Areas: <c>harness</c> echoes its
/// input; <c>expressions-v0</c> translates one schema expression (headers below); <c>ddl</c> converts a
/// script of CREATE statements.
/// </summary>
public static class GoldenTranslator
{
    public static GoldenResult Translate(string area, string tsql, IReadOnlyDictionary<string, string> headers)
    {
        var options = headers.TryGetValue("options", out var json) ? ConversionOptions.FromJson(json) : new ConversionOptions();
        return area.Split('/')[0] switch
        {
            "expressions-v0" => SchemaExpression(tsql, headers, options),
            "ddl" => Ddl(tsql, options),
            _ => new GoldenResult(tsql, []),
        };
    }

    /// <summary>
    /// A script of CREATE statements, read by the script reader and converted; the output is every
    /// non-empty phase script in order, and the diagnostics are every object's plus the run's.
    /// </summary>
    private static GoldenResult Ddl(string tsql, ConversionOptions options)
    {
        var read = SqlServerScriptReader.Read([("case.sql", tsql)]);
        var result = SchemaConverter.Convert(read.Database, options);
        string sql = string.Concat(Enum.GetValues<Phase>()
            .Where(p => result.Objects.Any(o => o.Phase == p))
            .Select(p => ScriptPlanner.PhaseScript(result, p) + "\n"));
        var diagnostics = read.Diagnostics.Concat(result.Diagnostics).Concat(result.Objects.SelectMany(o => o.Diagnostics));
        return new GoldenResult(sql, diagnostics.Select(d => new GoldenDiagnostic(d.Code, d.Severity.ToString())).ToList());
    }

    /// <summary>
    /// Headers: <c>columns</c> declares the table's columns as in CREATE TABLE
    /// (<c>qty int, name nvarchar(50)</c>); <c>kind</c> is <c>default</c> (the default), <c>check</c>
    /// or <c>filter</c> (predicates); <c>target</c> is the column type a default is for.
    /// </summary>
    private static GoldenResult SchemaExpression(string tsql, IReadOnlyDictionary<string, string> headers, ConversionOptions options)
    {
        var table = new ObjectName("dbo", "t");
        var columns = headers.TryGetValue("columns", out var cols)
            ? SqlServerScriptReader.Read([("columns", $"CREATE TABLE dbo.t ({cols});")]).Database.Tables.Single().Columns
            : [];
        SqlServerType? target = headers.TryGetValue("target", out var t)
            ? SqlServerScriptReader.Read([("target", $"CREATE TABLE dbo.x (c {t});")]).Database.Tables.Single().Columns.Single().Type
            : null;
        bool predicate = headers.TryGetValue("kind", out var kind) && kind is "check" or "filter";

        var names = new NameMapper(options);
        var translator = new SchemaExpressionTranslator(options, names, new TypeMapper(options, names));
        var result = translator.Translate(tsql.Trim(), table, columns, target, predicate);
        string sql = result.Expression is null ? "-- untranslated" : PgWriter.Expression(result.Expression);
        return new GoldenResult(sql + "\n", result.Diagnostics.Select(d => new GoldenDiagnostic(d.Code, d.Severity.ToString())).ToList());
    }
}
