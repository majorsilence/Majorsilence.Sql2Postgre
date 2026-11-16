using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>Views: each CREATE VIEW's query through the query translator.</summary>
public sealed partial class SchemaTranslator
{
    /// <summary>
    /// A view as CREATE VIEW (or CREATE MATERIALIZED VIEW for an indexed view). WITH SCHEMABINDING
    /// has no counterpart and is dropped; WITH CHECK OPTION is kept; TOP 100 PERCENT … ORDER BY is
    /// dropped, since SQL Server does not keep that order either.
    /// </summary>
    private TranslatedObject View(Module m, SemanticModel model, QueryTranslator queries)
    {
        var name = _names.Relation(m.Name);
        var dependsOn = model.Dependencies.Where(d => ObjectNameComparer.Instance.Equals(d.From, m.Name)).Select(d => d.To).ToList();
        if (model.ModuleTree(m.Name) is not ViewStatementBody view)
            return Untranslated(m, name.Qualified, dependsOn, DiagnosticCodes.ParseError, $"The definition of {m.Name} could not be parsed.");

        var context = new ExpressionContext(new Scope(), m.Name.Schema, m.Name);
        try
        {
            var select = view.SelectStatement;
            var query = queries.Query(DropPointlessOrder(select.QueryExpression, context), context, select.WithCtesAndXmlNamespaces);
            if (view.ViewOptions.Any(o => o.OptionKind is ViewOptionKind.SchemaBinding or ViewOptionKind.ViewMetadata))
                context.Report(DiagnosticCodes.ViewOptionDropped, Severity.Info,
                    "WITH SCHEMABINDING / VIEW_METADATA are dropped; PostgreSQL views always depend on what they read.");
            if (m.IsIndexed)
                context.Report(DiagnosticCodes.MaterializedViewRefresh, Severity.Warning,
                    $"{m.Name} is an indexed view, so it becomes a materialized view; SQL Server kept it current, PostgreSQL needs REFRESH MATERIALIZED VIEW after changes.");
            var statement = new PgCreateView(name, view.Columns.Select(c => _names.Alias(c.Value)).ToList(), query,
                Materialized: m.IsIndexed, CheckOption: view.WithCheckOption);
            return new TranslatedObject(m.Name, ObjectKind.View, name.Qualified, Phase.Code, [statement], context.Diagnostics, dependsOn)
            {
                Extensions = context.Extensions.ToList(),
                CompatFunctions = context.CompatFunctions.ToList(),
            };
        }
        catch (UntranslatableException ex)
        {
            return Untranslated(m, name.Qualified, dependsOn, ex.Code, ex.Message, context.Diagnostics);
        }
    }

    /// <summary>TOP (100) PERCENT … ORDER BY in a view orders nothing in SQL Server; the TOP and ORDER BY go.</summary>
    private static QueryExpression DropPointlessOrder(QueryExpression q, ExpressionContext c)
    {
        if (q is QuerySpecification { TopRowFilter: { Percent: true, WithTies: false, Expression: IntegerLiteral { Value: "100" } } } spec)
        {
            spec.TopRowFilter = null;
            spec.OrderByClause = null;
            c.Report(DiagnosticCodes.ViewOrderIgnored, Severity.Info,
                "TOP 100 PERCENT … ORDER BY in a view is dropped: SQL Server does not return the view's rows in that order either.");
        }
        return q;
    }

    private static TranslatedObject Untranslated(Module m, string target, IReadOnlyList<ObjectName> dependsOn, string code, string message,
        IReadOnlyList<Diagnostic>? earlier = null) =>
        new(m.Name, m.Kind, target, m.Kind == ObjectKind.Trigger ? Phase.Triggers : Phase.Code,
            [new PgUntranslated(code, m.Definition)],
            [.. earlier ?? [], new Diagnostic(code, Severity.Error, m.Name, message)], dependsOn);

    /// <summary>
    /// A view or routine that uses a view or routine that is Manual or skipped cannot work; it
    /// becomes Manual too, and so on down the chain.
    /// </summary>
    private static List<TranslatedObject> PropagateUnconverted(List<TranslatedObject> objects, SemanticModel model)
    {
        var bySource = objects.GroupBy(o => o.Source, ObjectNameComparer.Instance).ToDictionary(g => g.Key, g => g.ToList(), ObjectNameComparer.Instance);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                if (o.Kind is not (ObjectKind.View or ObjectKind.Function or ObjectKind.Procedure) || o.Status is ObjectStatus.Manual or ObjectStatus.Skipped)
                    continue;
                var blocker = o.DependsOn.FirstOrDefault(d =>
                    bySource.TryGetValue(d, out var deps)
                    && deps.Any(x => x.Kind is ObjectKind.View or ObjectKind.Function or ObjectKind.Procedure && x.Status is ObjectStatus.Manual or ObjectStatus.Skipped));
                if (blocker is null)
                    continue;
                string why = $"{o.Source} uses {blocker}, which is not converted.";
                objects[i] = o with { Diagnostics = [.. o.Diagnostics, new Diagnostic(DiagnosticCodes.DependsOnUnconverted, Severity.Error, o.Source, why)] };
                bySource[o.Source] = bySource[o.Source].Select(x => ReferenceEquals(x, o) ? objects[i] : x).ToList();
                changed = true;
            }
        }
        return objects;
    }
}
