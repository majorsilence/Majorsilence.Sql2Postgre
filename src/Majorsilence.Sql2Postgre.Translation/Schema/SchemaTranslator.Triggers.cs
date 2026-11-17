using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>
/// DML triggers. Each event of a trigger (INSERT, UPDATE, DELETE) becomes a trigger function and
/// a CREATE TRIGGER: PostgreSQL allows transition tables only on single-event triggers, and the
/// function for one event can then leave out the pseudo-table the event does not have.
/// </summary>
public sealed partial class SchemaTranslator
{
    private static string? TriggerEventName(TriggerActionType type) => type switch
    {
        TriggerActionType.Insert => "INSERT",
        TriggerActionType.Update => "UPDATE",
        TriggerActionType.Delete => "DELETE",
        _ => null,
    };

    /// <summary>How many triggers each table has for each event (several fire in name order in PostgreSQL).</summary>
    private static Dictionary<string, int> TriggerCounts(IEnumerable<Module> modules, SemanticModel model)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules.Where(m => m.Kind == ObjectKind.Trigger))
        {
            if (model.ModuleTree(m.Name) is not TriggerStatementBody t || t.TriggerType == TriggerType.InsteadOf)
                continue;
            var table = m.ParentTable ?? model.ResolveRelation(t.TriggerObject.Name, m.Name.Schema);
            foreach (var action in t.TriggerActions)
                if (TriggerEventName(action.TriggerActionType) is { } e)
                    counts[$"{table}|{e}"] = counts.GetValueOrDefault($"{table}|{e}") + 1;
        }
        return counts;
    }

    private TranslatedObject Trigger(Module m, SemanticModel model, ProceduralTranslator procedural, IReadOnlyDictionary<string, int> counts)
    {
        var dependsOn = model.Dependencies.Where(d => ObjectNameComparer.Instance.Equals(d.From, m.Name)).Select(d => d.To).ToList();
        string target = _names.Routine(m.Name).Qualified;
        if (model.ModuleTree(m.Name) is not TriggerStatementBody t)
            return Untranslated(m, target, dependsOn, DiagnosticCodes.ParseError, $"The definition of {m.Name} could not be parsed.");
        if (t.TriggerObject.TriggerScope != TriggerScope.Normal)
            return Untranslated(m, target, dependsOn, DiagnosticCodes.DdlTrigger,
                $"{m.Name} is a DDL or logon trigger. PostgreSQL's event triggers fire on DDL commands with a different model; rewrite it as one if it is still needed.");
        if (t.MethodSpecifier is not null)
            return Untranslated(m, target, dependsOn, DiagnosticCodes.ClrObject, $"{m.Name} is a CLR trigger: .NET code, not T-SQL.");
        var tableName = m.ParentTable ?? model.ResolveRelation(t.TriggerObject.Name, m.Name.Schema);
        var relation = tableName is null ? null : model.Relation(tableName);
        if (tableName is null || relation is null)
            return Untranslated(m, target, dependsOn, DiagnosticCodes.DependsOnUnconverted, $"{m.Name} is on {t.TriggerObject.Name.BaseIdentifier.Value}, which is not in the source database.");
        bool onView = model.Table(tableName) is null;
        bool insteadOf = t.TriggerType == TriggerType.InsteadOf;
        if (insteadOf && !onView)
            return Untranslated(m, target, dependsOn, DiagnosticCodes.InsteadOfTable,
                $"{m.Name} is an INSTEAD OF trigger on the table {tableName}. PostgreSQL has INSTEAD OF triggers on views only: use a BEFORE trigger that returns NULL, or a rule, or move the logic to the code that writes the table.");
        dependsOn.Add(tableName);

        var events = t.TriggerActions.Select(a => TriggerEventName(a.TriggerActionType)).OfType<string>().Distinct().ToList();
        var context = new ExpressionContext(new Scope(), m.Name.Schema, m.Name);
        var pgTable = _names.Relation(tableName);
        var statements = new List<PgStatement>();
        try
        {
            if (insteadOf && !IsSingleStatement(t.StatementList?.Statements ?? []))
                context.Report(DiagnosticCodes.InsteadOfPerRow, Severity.Warning,
                    $"{m.Name} runs once per row in PostgreSQL (INSTEAD OF triggers on views are FOR EACH ROW) and once per statement in SQL Server: "
                    + "its body sees one row in inserted/deleted each time. Check code that counts or aggregates them.");
            if (t.Options.Count > 0)
                context.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info,
                    $"Trigger options ({string.Join(", ", t.Options.Select(o => o.OptionKind))}) are dropped.");
            if (t.IsNotForReplication)
                context.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, "NOT FOR REPLICATION is dropped: PostgreSQL's logical replication applies changes with triggers off by default.");
            foreach (string e in events)
            {
                string suffix = events.Count == 1 ? "" : "_" + e.ToLowerInvariant();
                var function = _names.Routine(new ObjectName(m.Name.Schema, m.Name.Name + suffix));
                var key = model.Table(tableName)?.PrimaryKey?.Columns.Select(k => _names.Column(tableName, k.Name)).ToList() ?? [];
                var trigger = new TriggerEvent(pgTable, e, insteadOf, relation.Columns, key,
                    column => onView ? _names.Alias(column) : _names.Column(tableName, column));
                var signature = new BodySignature(BodyKind.Trigger, function.Name, []) { Trigger = trigger };
                var inner = new ExpressionContext(new Scope(), m.Name.Schema, m.Name);
                var body = procedural.Body(t.StatementList?.Statements ?? [], signature, inner);
                MergeInto(context, inner);
                statements.Add(new PgCreateRoutine(function, false, [], body) { Returns = "trigger" });
                statements.Add(new PgCreateTrigger(_names.Trigger(tableName, m.Name.Name + suffix), pgTable, insteadOf ? "INSTEAD OF" : "AFTER", e, function)
                {
                    NewTable = insteadOf || !trigger.HasInserted ? null : "inserted",
                    OldTable = insteadOf || !trigger.HasDeleted ? null : "deleted",
                    ForEachRow = insteadOf,
                });
                if (!insteadOf && counts.GetValueOrDefault($"{tableName}|{e}") > 1)
                    context.Report(DiagnosticCodes.TriggerOrder, Severity.Info,
                        $"{tableName} has several {e} triggers: PostgreSQL fires them in name order, SQL Server in its own order (sp_settriggerorder). Rename them if the order matters.");
            }
            return new TranslatedObject(m.Name, ObjectKind.Trigger, target, Phase.Triggers, statements, context.Diagnostics, dependsOn)
            {
                Extensions = context.Extensions.ToList(),
                CompatFunctions = context.CompatFunctions.ToList(),
            };
        }
        catch (UntranslatableException ex)
        {
            return Untranslated(m, target, dependsOn, ex.Code, ex.Message, context.Diagnostics);
        }
    }

    /// <summary>A body of one statement (inside any BEGIN … END), ignoring SET options.</summary>
    private static bool IsSingleStatement(IList<TSqlStatement> statements)
    {
        while (statements is [BeginEndBlockStatement block])
            statements = block.StatementList.Statements;
        return statements.Count(s => s is not (PredicateSetStatement or SetCommandStatement)) == 1;
    }

    /// <summary>One event's diagnostics into the trigger's, each message once.</summary>
    private static void MergeInto(ExpressionContext into, ExpressionContext from)
    {
        foreach (var d in from.Diagnostics)
            if (!into.Diagnostics.Any(x => x.Code == d.Code && x.Message == d.Message))
                into.Diagnostics.Add(d);
        into.Extensions.UnionWith(from.Extensions);
        into.CompatFunctions.UnionWith(from.CompatFunctions);
    }
}
