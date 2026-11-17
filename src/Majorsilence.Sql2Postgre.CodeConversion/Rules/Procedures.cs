using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>A stored procedure the code calls: its converted name, and its shape when the schema is known.</summary>
internal sealed record ProcedureTarget(PgName Name, ProcedureShape? Shape);

/// <summary>Resolves procedure names written in code against the source database, as the schema converter converts them.</summary>
internal sealed class Procedures(CodeConversionOptions options)
{
    private readonly NameMapper _names = new(options.Schema);
    private ProceduralTranslator? _procedural;

    /// <summary>The procedure a name in code (<c>dbo.GetOrders</c>, <c>[Sales].[Get Orders]</c>, <c>GetOrders</c>) calls, or null when it is not a name.</summary>
    public ProcedureTarget? Resolve(string written)
    {
        var parsed = new TSql160Parser(true).Parse(new StringReader("EXEC " + written.Trim().TrimEnd(';')), out var errors);
        if (errors.Count > 0 || parsed is not TSqlScript { Batches: [{ Statements: [ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } name } }] }] })
            return null;
        if (options.Model is { } model && model.ResolveRoutine(name, null) is { } routine)
        {
            _procedural ??= Translator(model);
            return new ProcedureTarget(_names.Routine(routine), _procedural.Shape(routine));
        }
        var source = new ObjectName(name.SchemaIdentifier?.Value ?? "dbo", name.BaseIdentifier.Value);
        return new ProcedureTarget(_names.Routine(source), null);
    }

    private ProceduralTranslator Translator(Translation.Semantics.SemanticModel model)
    {
        var expressions = new ExpressionTranslator(options.Schema, _names, new TypeMapper(options.Schema, _names), model);
        return new ProceduralTranslator(new QueryTranslator(expressions));
    }
}
