using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Schema;

namespace Majorsilence.Sql2Postgre.Postgres;

/// <summary>
/// Converts a database's schema to PostgreSQL: translates every object, writes its statements
/// for the target version, and orders the result (see <see cref="ScriptPlanner"/>).
/// </summary>
public static class SchemaConverter
{
    public static ConversionResult Convert(SourceDatabase db, ConversionOptions options)
    {
        var translator = new SchemaTranslator(options);
        var translated = translator.Translate(db);
        var objects = translated.Select(o => new ConvertedObject(o.Source, o.Kind, o.TargetName, o.Phase,
                string.Concat(o.Statements.Select(s => PgWriter.Write(s, options.TargetVersion))),
                o.Status, o.Diagnostics, o.DependsOn)
            { Extensions = o.Extensions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() })
            .ToList();
        var (ordered, planDiagnostics) = ScriptPlanner.Order(objects);
        return new ConversionResult(ordered, [.. translator.Names.Diagnostics, .. planDiagnostics]);
    }
}
