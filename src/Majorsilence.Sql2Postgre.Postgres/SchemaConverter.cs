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
        var compat = CompatObjects(translator.UsedCompatFunctions, options);
        var objects = translated.Select(o => new ConvertedObject(o.Source, o.Kind, o.TargetName, o.Phase,
                string.Concat(o.Statements.Select(s => PgWriter.Write(s, options.TargetVersion))),
                o.Status, o.Diagnostics, o.DependsOn)
            {
                Extensions = o.Extensions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                CompatFunctions = o.CompatFunctions,
            })
            .Concat(compat)
            .ToList();
        var (ordered, planDiagnostics) = ScriptPlanner.Order(objects);
        return new ConversionResult(ordered, [.. translator.Names.Diagnostics, .. planDiagnostics]);
    }

    /// <summary>The compat schema and each helper used, as objects of the Schemas and Compat phases.</summary>
    public static IEnumerable<ConvertedObject> CompatObjects(IReadOnlyCollection<string> used, ConversionOptions options)
    {
        if (used.Count == 0)
            yield break;
        string schema = Translation.Names.NameMapper.Quote(options.CompatSchemaName);
        yield return new ConvertedObject(new ObjectName(options.CompatSchemaName, options.CompatSchemaName), ObjectKind.Schema, schema,
            Phase.Schemas, $"CREATE SCHEMA IF NOT EXISTS {schema};\n", ObjectStatus.Converted, [], []);
        foreach (string name in used.Order(StringComparer.Ordinal))
            yield return new ConvertedObject(new ObjectName(options.CompatSchemaName, name), ObjectKind.Function, $"{schema}.{name}",
                Phase.Compat, Compat.CompatScripts.Create(name, schema), ObjectStatus.Converted, [], []);
    }
}
