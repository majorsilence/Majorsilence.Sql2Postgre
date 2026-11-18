using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Full-text search over the GIN index the schema converter builds (one <c>to_tsvector</c>
/// expression per column). <c>CONTAINS</c> and <c>FREETEXT</c> test each searched column on its
/// own, since SQL Server matches a search condition within one column: <c>v1 @@ q OR v2 @@ q</c>.
/// The condition becomes a tsquery through <c>mssql_compat.contains_query</c> or
/// <c>freetext_query</c>; for a constant PostgreSQL works it out once while planning, and for a
/// variable a scalar subquery works it out once per statement. <c>CONTAINSTABLE</c> and
/// <c>FREETEXTTABLE</c> are a derived table of the matching rows' key (<c>KEY</c>) and
/// <c>ts_rank</c> scaled to SQL Server's 0–1000 (<c>RANK</c>).
/// </summary>
public sealed partial class ExpressionTranslator
{
    /// <summary>One column a search reads: the column's PostgreSQL name, type, and its index configuration.</summary>
    private sealed record SearchColumn(string Name, SqlServerType Type, string Config);

    private PgExpr FullTextPredicate(FullTextPredicate ft, ExpressionContext c)
    {
        if (ft.PropertyName is not null)
            throw Untranslatable(ft, "a full-text search of a document property");
        var (table, binding) = FullTextTarget(ft.Columns, ft, c);
        var columns = SearchColumns(table, ft.Columns, ft, c);
        string? qualifier = c.QualifyColumns && binding is not null ? AliasFor(binding) : null;
        bool contains = ft.FullTextFunctionType == FullTextFunctionType.Contains;
        PgExpr? match = null;
        foreach (var column in columns)
        {
            var test = new PgBinary("@@", FullTextSearch.Vector(column.Config, new PgColumnRef(column.Name, qualifier), column.Type),
                SearchQuery(contains, column.Config, ft.Value, ft.LanguageTerm, ft, c));
            match = match is null ? test : new PgBinary("OR", match, test);
        }
        ReportFullText(contains ? "CONTAINS" : "FREETEXT", table.Name, c);
        return match!;
    }

    /// <summary><c>CONTAINSTABLE</c> / <c>FREETEXTTABLE (table, columns, condition [, LANGUAGE l] [, top_n])</c> as a derived table.</summary>
    public PgFromItem FullTextTable(FullTextTableReference r, ExpressionContext c)
    {
        if (r.PropertyName is not null)
            throw Untranslatable(r, "a full-text search of a document property");
        var name = model.ResolveRelation(r.TableName, c.Schema);
        if (name is null || model.Table(name) is not { } table)
            throw Untranslatable(r, "a full-text search of a table not in this database");
        var index = FullTextIndexOf(table) ?? throw Untranslatable(r, $"a full-text search of {name}, which has no full-text index");
        if (index.FullText?.KeyColumn is not { } key)
            throw Untranslatable(r, $"a full-text table on {name}, whose full-text KEY INDEX column is not known");
        var columns = SearchColumns(table, r.Columns, r, c);
        bool contains = r.FullTextFunctionType == FullTextFunctionType.Contains;
        PgExpr? match = null;
        PgExpr? document = null;
        foreach (var column in columns)
        {
            var vector = FullTextSearch.Vector(column.Config, new PgColumnRef(column.Name), column.Type);
            var test = new PgBinary("@@", vector, SearchQuery(contains, column.Config, r.SearchCondition, r.Language, r, c));
            match = match is null ? test : new PgBinary("OR", match, test);
            var part = columns.Count == 1 ? vector : new PgFunctionCall("COALESCE", [vector, new PgCast(PgLiteral.String(""), "tsvector")]);
            document = document is null ? part : new PgBinary("||", document, part);
        }
        var rank = new PgCast(new PgBinary("*", new PgFunctionCall("ts_rank",
            [document!, SearchQuery(contains, columns[0].Config, r.SearchCondition, r.Language, r, c)]), new PgLiteral("1000")), "integer");
        string rankName = names.Alias("RANK");
        var select = new PgSelect
        {
            Targets = [new PgTarget(new PgColumnRef(names.Column(name, key)), names.Alias("KEY")), new PgTarget(rank, rankName)],
            From = [new PgTableRef(names.Relation(name))],
            Where = match,
            OrderBy = r.TopN is null ? [] : [new PgSortKey(new PgColumnRef(rankName), Descending: true)],
            Limit = r.TopN is null ? null : Scalar(r.TopN, c),
        };
        ReportFullText(contains ? "CONTAINSTABLE" : "FREETEXTTABLE", name, c);
        return new PgSubqueryRef(select, names.Alias(r.Alias?.Value ?? "ft"), []);
    }

    /// <summary>
    /// The tsquery a search condition becomes in a configuration: the column's own, unless the
    /// search gives a LANGUAGE (which in SQL Server, too, only changes how the condition is read).
    /// </summary>
    private PgExpr SearchQuery(bool contains, string columnConfig, ScalarExpression condition, ScalarExpression? language, TSqlFragment source, ExpressionContext c)
    {
        PgExpr config = PgLiteral.String(columnConfig);
        if (language is not null)
        {
            if (language is Literal literal && FullTextLanguages.Parse(literal is StringLiteral s ? s.Value : literal.Value) is { } lcid)
            {
                string? mapped = FullTextSearch.Config(lcid);
                if (mapped is null)
                    c.Report(DiagnosticCodes.FullTextSettingUnmapped, Severity.Warning,
                        $"LANGUAGE {FullTextLanguages.Name(lcid)} has no PostgreSQL text search configuration; the search condition is read with simple (no stemming, no stop words).");
                config = PgLiteral.String(mapped ?? "simple");
            }
            else if (language is Literal)
                throw Untranslatable(language, "a full-text LANGUAGE SQL Server does not list");
            else
                config = Compat("fulltext_config", [Scalar(language, c)], c, source);
        }
        var query = Compat(contains ? "contains_query" : "freetext_query", [config, Text(condition, c)], c, source);
        // A constant is worked out once while planning; anything else once per statement, not per row.
        return condition is Literal && language is null or Literal
            ? query
            : new PgSubquery(new PgSelect { Targets = [new PgTarget(query)] });
    }

    /// <summary>
    /// The table a CONTAINS or FREETEXT searches: the one its columns belong to, or for <c>*</c> the
    /// one table in the FROM clause with a full-text index.
    /// </summary>
    private (Table Table, Binding? Binding) FullTextTarget(IList<ColumnReferenceExpression> columns, TSqlFragment source, ExpressionContext c)
    {
        Binding? binding = null;
        foreach (var col in columns)
        {
            var parts = col.MultiPartIdentifier?.Identifiers.Select(i => i.Value).ToList() ?? [];
            Binding? found = col.ColumnType == ColumnType.Wildcard
                ? parts.Count > 0 ? c.Scope.ResolveQualifier(parts[^1], parts.Count > 1 ? parts[^2] : null) : null
                : c.Scope.ResolveColumn(parts)?.Binding;
            if (found is null && col.ColumnType == ColumnType.Wildcard && parts.Count == 0)
                continue;
            if (found is null || (binding is not null && !ReferenceEquals(found, binding)))
                throw Untranslatable(source, "a full-text search whose columns are not all of one table here");
            binding = found;
        }
        if (binding is null)
        {
            var candidates = c.Scope.Bindings.Where(b => b.Relation is { Kind: ObjectKind.Table, Name: { } n } && model.Table(n) is { } t && FullTextIndexOf(t) is not null).ToList();
            if (candidates.Count != 1)
                throw Untranslatable(source, "a full-text search of * with no single full-text indexed table in FROM");
            binding = candidates[0];
        }
        if (binding.Relation is not { Kind: ObjectKind.Table, Name: { } name } || model.Table(name) is not { } table || FullTextIndexOf(table) is null)
            throw Untranslatable(source, "a full-text search of something other than a table with a full-text index");
        return (table, binding);
    }

    /// <summary>The searched columns (<c>*</c> is every column of the index) that the converted index has.</summary>
    private List<SearchColumn> SearchColumns(Table table, IList<ColumnReferenceExpression> columns, TSqlFragment source, ExpressionContext c)
    {
        var index = FullTextIndexOf(table)!;
        var info = index.FullText ?? new FullTextIndex(null, index.Keys.Select(k => new FullTextColumn(k.Name)).ToList());
        bool all = columns.Any(col => col.ColumnType == ColumnType.Wildcard);
        var wanted = all
            ? info.Columns
            : columns.Select(col => col.MultiPartIdentifier.Identifiers[^1].Value).Select(n =>
                info.Columns.FirstOrDefault(fc => fc.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                ?? throw Untranslatable(source, $"a full-text search of {table.Name}.{n}, which the full-text index does not have")).ToList();
        var result = new List<SearchColumn>();
        foreach (var column in wanted)
        {
            var type = table.Columns.FirstOrDefault(tc => tc.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase))?.Type;
            if (type is null || !FullTextSearch.IsText(type))
            {
                if (!all)
                    throw Untranslatable(source, $"a full-text search of the {type?.Name ?? "unknown"} documents in {table.Name}.{column.Name}, which PostgreSQL cannot index (S2P2021)");
                c.Report(DiagnosticCodes.FullTextSearch, Severity.Warning,
                    $"The full-text search of {table.Name} leaves out {column.Name}: its {type?.Name} documents are not indexed in PostgreSQL (S2P2021).");
                continue;
            }
            result.Add(new SearchColumn(names.Column(table.Name, column.Name), type, FullTextSearch.ColumnConfig(column)));
        }
        if (result.Count == 0)
            throw Untranslatable(source, $"a full-text search of {table.Name}, none of whose searched columns PostgreSQL can index");
        return result;
    }

    private static TableIndex? FullTextIndexOf(Table table) => table.Indexes.FirstOrDefault(i => i.Type == "fulltext");

    private static void ReportFullText(string what, ObjectName table, ExpressionContext c)
    {
        string message = $"{what} on {table} uses PostgreSQL text search. Which rows match is close to SQL Server's, but words are stemmed by the column's configuration "
            + "(so CONTAINS also matches other forms of a word), stop words differ, a thesaurus and weights are ignored, and ranks are ts_rank's, not SQL Server's.";
        if (!c.Diagnostics.Any(d => d.Code == DiagnosticCodes.FullTextSearch && d.Message == message))
            c.Report(DiagnosticCodes.FullTextSearch, Severity.Info, message);
    }
}
